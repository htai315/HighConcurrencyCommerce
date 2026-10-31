import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

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
const emptyCart = { id: 'cart-checkout', items: [], totalQuantity: 0, subtotal: 0, canCheckout: false, updatedAt: '2026-10-30T00:01:00Z' }
const result = {
  orderId: 'order-1', orderNumber: 'HC-20261030-ABC123', orderStatus: 'PENDING_COD_CONFIRMATION',
  paymentMethod: 'COD', paymentStatus: 'COD_PENDING', totalAmount: 4800000, deadlineAt: '2026-10-31T00:00:00Z',
  shippingAddress: { recipientName: address.recipientName, phone: address.phone, addressLine: address.addressLine, ward: address.ward, province: address.province },
  items: [{ productSkuId: 'sku-racket', productTitle: 'Vợt High Pro', skuCode: 'RACKET-4U', optionLabel: '4U · G5', quantity: 2, unitPrice: 2400000, lineTotal: 4800000 }],
}

let router: ReturnType<typeof createMemoryRouter> | undefined
function handlers() {
  let cartReads = 0
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/account/addresses`, () => HttpResponse.json([address])),
    http.get(`${root}/cart`, () => HttpResponse.json(cartReads++ === 0 ? cart : emptyCart)),
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
  server.use(http.post(`${root}/checkouts`, async ({ request }) => {
    requestBody = await request.json()
    idempotencyKey = request.headers.get('Idempotency-Key')
    if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
    return HttpResponse.json(result, { status: 201 })
  }))
  const user = userEvent.setup()
  open()
  await user.click(await screen.findByRole('button', { name: 'Đặt hàng và giữ tồn' }))
  expect(await screen.findByRole('heading', { name: result.orderNumber })).toBeVisible()
  expect(screen.getByText('COD_PENDING')).toBeVisible()
  expect(idempotencyKey).toMatch(/^[0-9a-f-]{36}$/)
  expect(requestBody).toEqual({
    shippingAddressId: address.id, paymentMethod: 'COD',
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
  const button = await screen.findByRole('button', { name: 'Đặt hàng và giữ tồn' })
  await user.click(button)
  expect(await screen.findByText(/Chưa xác định được checkout đã hoàn tất/)).toBeVisible()
  await user.click(button)
  expect(await screen.findByRole('heading', { name: result.orderNumber })).toBeVisible()
  expect(keys).toHaveLength(2)
  expect(keys[0]).toBe(keys[1])
})
