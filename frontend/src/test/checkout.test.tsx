import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import { parseShippingQuoteResult } from '../features/shipping/shipping.api'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'customer-checkout', email: 'checkout@example.invalid', fullName: 'Khách checkout', role: 'CUSTOMER' }
const address = {
  id: 'address-checkout', recipientName: 'Nguyễn Cầu Lông', phone: '0901234567', addressLine: '17 Đường Sân Cầu',
  ward: 'Phường 1', province: 'TP Hồ Chí Minh', isDefault: true, createdAt: '2026-10-30T00:00:00Z',
}
const cart = {
  id: 'cart-checkout', items: [{
    productSkuId: 'sku-racket', productId: 'product-racket', productTitle: 'Vợt High Pro', skuCode: 'RACKET-4U',
    optionLabel: '4U · G5', unitPrice: 2400000, quantity: 2, lineTotal: 4800000,
    imageUrl: null, availabilityCode: 'AVAILABLE',
  }], totalQuantity: 2, subtotal: 4800000, canCheckout: true, updatedAt: '2026-10-30T00:00:00Z',
}
const result = {
  orderId: 'order-1', orderNumber: 'HC-20261030-ABC123', orderStatus: 'PENDING_COD_CONFIRMATION',
  paymentMethod: 'COD', paymentStatus: 'COD_PENDING', totalAmount: 4830000, subtotalAmount: 4800000, voucherDiscountAmount: 0, voucherCode: null,
  shippingFee: 30000, shippingMethod: { shippingMethodId: 'shipping-standard', code: 'STANDARD', name: 'Giao hàng tiêu chuẩn', estimatedMinDays: 2, estimatedMaxDays: 4, revision: 3, destinationProvinceKey: 'HO CHI MINH' },
  deadlineAt: '2026-10-31T00:00:00Z',
  shippingAddress: { recipientName: address.recipientName, phone: address.phone, addressLine: address.addressLine, ward: address.ward, province: address.province },
  items: [{ productSkuId: 'sku-racket', productTitle: 'Vợt High Pro', skuCode: 'RACKET-4U', optionLabel: '4U · G5', quantity: 2, unitPrice: 2400000, lineTotal: 4800000 }],
}

let router: ReturnType<typeof createMemoryRouter> | undefined
function handlers() {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/account/addresses`, () => HttpResponse.json([address])),
    // AppShell/SiteHeader and cartLoader may read concurrently. The response must
    // not depend on which consumer wins that race.
    http.get(`${root}/cart`, () => HttpResponse.json(cart)),
  )
}
function open() {
  router = createMemoryRouter(appRoutes, { initialEntries: ['/cart'] })
  render(<RouterProvider router={router} />)
}
afterEach(() => { router?.dispose(); sessionStorage.clear() })

test('Customer checkout gửi explicit lines, CSRF và Idempotency-Key rồi hiển thị đơn đã giữ tồn', async () => {
  handlers()
  let requestBody: unknown
  let idempotencyKey: string | null = null
  server.use(
    http.post(`${root}/shipping/quotes`, () => HttpResponse.json({
      shippingAddressId: address.id, destinationProvinceKey: 'HO CHI MINH', merchandiseSubtotal: 4800000,
      quotes: [
        { shippingMethodId: 'shipping-standard', code: 'STANDARD', name: 'Giao hàng tiêu chuẩn', description: null, shippingFee: 30000, baseFee: 20000, provinceSurcharge: 10000, freeShippingApplied: false, freeShippingThreshold: null, estimatedMinDays: 2, estimatedMaxDays: 4, methodRevision: 3, isDefault: true },
        { shippingMethodId: 'shipping-express', code: 'EXPRESS', name: 'Giao hàng hỏa tốc', description: 'Ưu tiên xử lý', shippingFee: 60000, baseFee: 50000, provinceSurcharge: 10000, freeShippingApplied: false, freeShippingThreshold: null, estimatedMinDays: 0, estimatedMaxDays: 1, methodRevision: 7, isDefault: false },
      ],
    })),
    http.post(`${root}/checkouts`, async ({ request }) => {
      requestBody = await request.json()
      idempotencyKey = request.headers.get('Idempotency-Key')
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
      return HttpResponse.json({ ...result, totalAmount: 4860000, shippingFee: 60000, shippingMethod: { ...result.shippingMethod, shippingMethodId: 'shipping-express', code: 'EXPRESS', name: 'Giao hàng hỏa tốc', estimatedMinDays: 0, estimatedMaxDays: 1, revision: 7 } }, { status: 201 })
    }),
  )
  const user = userEvent.setup()
  open()
  const standard = await screen.findByRole('radio', { name: /Giao hàng tiêu chuẩn/ })
  expect(standard).toBeChecked()
  await user.click(screen.getByRole('radio', { name: /Giao hàng hỏa tốc/ }))
  const checkoutButton = screen.getByRole('button', { name: 'Đặt hàng' })
  expect(checkoutButton).toBeEnabled()
  await user.click(checkoutButton)
  expect(await screen.findByRole('heading', { name: result.orderNumber })).toBeVisible()
  expect(screen.getByText('Thanh toán khi nhận hàng')).toBeVisible()
  expect(idempotencyKey).toMatch(/^[0-9a-f-]{36}$/)
  expect(requestBody).toEqual({
    shippingAddressId: address.id, paymentMethod: 'COD',
    shippingMethodId: 'shipping-express', shippingMethodRevision: 7, quotedShippingFee: 60000,
    lines: [{ productSkuId: 'sku-racket', quantity: 2, flashSaleOfferId: null }],
  })
  expect(sessionStorage.getItem('hcc.checkout-intent.v1')).toBeNull()
})

test('lỗi mạng giữ nguyên Idempotency-Key để retry không tạo đơn trùng', async () => {
  handlers()
  const keys: Array<string | null> = []
  let calls = 0
  server.use(http.post(`${root}/checkouts`, ({ request }) => {
    keys.push(request.headers.get('Idempotency-Key'))
    calls += 1
    return calls === 1
      ? HttpResponse.json({ code: 'SERVER_ERROR' }, { status: 500 })
      : HttpResponse.json(result, { status: 200, headers: { 'Idempotency-Replayed': 'true' } })
  }))
  const user = userEvent.setup()
  open()
  await screen.findByRole('radio', { name: /Giao hàng tiêu chuẩn/ })
  const button = screen.getByRole('button', { name: 'Đặt hàng' })
  await user.click(button)
  expect(await screen.findByText(/Chưa xác định được checkout đã hoàn tất/)).toBeVisible()
  await user.click(button)
  expect(await screen.findByRole('heading', { name: result.orderNumber })).toBeVisible()
  expect(keys).toHaveLength(2)
  expect(keys[0]).toBe(keys[1])
})

test('shipping quote parser từ chối ETA hoặc revision không hợp lệ', () => {
  expect(() => parseShippingQuoteResult({
    shippingAddressId: address.id, destinationProvinceKey: 'HO CHI MINH', merchandiseSubtotal: 100000,
    quotes: [{ shippingMethodId: 'method', code: 'STANDARD', name: 'Tiêu chuẩn', description: null, shippingFee: 20000, baseFee: 20000, provinceSurcharge: 0, freeShippingApplied: false, freeShippingThreshold: null, estimatedMinDays: 5, estimatedMaxDays: 2, methodRevision: 0, isDefault: true }],
  })).toThrow()
})

test('PayOS chỉ được chọn khi capability endpoint xác nhận feature đã bật', async () => {
  handlers()
  server.use(
    http.get(`${root}/payments/providers`, () => HttpResponse.json({ payOs: { enabled: true } })),
  )
  open()

  const payOs = await screen.findByRole('radio', { name: /Chuyển khoản nhanh PayOS/ })
  expect(payOs).toBeEnabled()
  await userEvent.setup().click(payOs)
  expect(payOs).toBeChecked()
  expect(screen.getByRole('button', { name: 'Đặt hàng & mở PayOS' })).toBeEnabled()
})

test('quote thay đổi tải lại phí và bắt Customer chọn lại, không tự checkout', async () => {
  handlers()
  let quoteCalls = 0
  let checkoutCalls = 0
  server.use(
    http.post(`${root}/shipping/quotes`, async ({ request }) => {
      quoteCalls += 1
      const body = await request.json() as { shippingAddressId: string }
      const fee = quoteCalls === 1 ? 30000 : 45000
      return HttpResponse.json({
        shippingAddressId: body.shippingAddressId, destinationProvinceKey: 'HO CHI MINH', merchandiseSubtotal: 4800000,
        quotes: [{ shippingMethodId: 'shipping-standard', code: 'STANDARD', name: 'Giao hàng tiêu chuẩn', description: null, shippingFee: fee, baseFee: fee, provinceSurcharge: 0, freeShippingApplied: false, freeShippingThreshold: null, estimatedMinDays: 2, estimatedMaxDays: 4, methodRevision: quoteCalls, isDefault: true }],
      })
    }),
    http.post(`${root}/checkouts`, () => {
      checkoutCalls += 1
      return HttpResponse.json({ code: 'SHIPPING_QUOTE_CHANGED', message: 'Phí đã đổi' }, { status: 409 })
    }),
  )
  const user = userEvent.setup()
  open()
  await screen.findByRole('radio', { name: /Giao hàng tiêu chuẩn/ })
  await user.click(screen.getByRole('button', { name: 'Đặt hàng' }))
  expect(await screen.findByText(/Phí vận chuyển vừa thay đổi/)).toBeVisible()
  expect(await screen.findByText(/45\.000/)).toBeVisible()
  expect(screen.getByRole('button', { name: 'Đặt hàng' })).toBeDisabled()
  expect(checkoutCalls).toBe(1)
  expect(quoteCalls).toBe(2)
  await user.click(screen.getByRole('radio', { name: /Giao hàng tiêu chuẩn/ }))
  expect(screen.getByRole('button', { name: 'Đặt hàng' })).toBeEnabled()
})
