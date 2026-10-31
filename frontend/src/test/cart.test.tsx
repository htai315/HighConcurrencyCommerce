import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { addGuestItem } from '../features/cart/guest-cart'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'customer-cart', email: 'cart@example.invalid', fullName: 'Người chơi cầu lông', role: 'CUSTOMER' }
const product = {
  id: 'product-racket', title: 'Vợt High Pro', description: 'Vợt thi đấu', category: null,
  skus: [{ id: 'sku-racket', skuCode: 'RACKET-4U', optionLabel: '4U · G5', price: 2400000, inStock: true }],
  images: [{ id: 'img-racket', fileKey: 'images/racket.webp', altText: 'Vợt High Pro', displayOrder: 1 }],
}
const serverCart = {
  id: 'cart-1', items: [{
    productSkuId: 'sku-racket', productId: 'product-racket', productTitle: 'Vợt High Pro', skuCode: 'RACKET-4U',
    optionLabel: '4U · G5', unitPrice: 2400000, quantity: 2, lineTotal: 4800000,
    imageUrl: 'images/racket.webp', availabilityCode: 'AVAILABLE',
  }], totalQuantity: 2, subtotal: 4800000, canCheckout: true, updatedAt: '2026-10-30T00:00:00Z',
}

let router: ReturnType<typeof createMemoryRouter> | undefined
function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}
afterEach(() => { router?.dispose(); localStorage.clear() })

test('Guest thêm sản phẩm từ chi tiết, lưu local và quản lý tại trang giỏ', async () => {
  server.use(http.get(`${root}/catalog/products/product-racket`, () => HttpResponse.json(product)))
  const user = userEvent.setup()
  const route = open('/products/product-racket')
  await user.click(await screen.findByRole('button', { name: 'Thêm vào giỏ' }))
  expect(screen.getByRole('status')).toHaveTextContent('Đã thêm vào giỏ hàng')
  await user.click(screen.getByRole('link', { name: 'Xem giỏ' }))
  await waitFor(() => expect(route.state.location.pathname).toBe('/cart'))
  expect(await screen.findByRole('heading', { name: 'Vợt High Pro', level: 2 })).toBeVisible()
  expect(screen.getByText('4U · G5 · RACKET-4U')).toBeVisible()
  expect(screen.getByRole('link', { name: 'Đăng nhập để tiếp tục' })).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Xóa' }))
  expect(await screen.findByRole('heading', { name: 'Giỏ hàng đang trống' })).toBeVisible()
})

test('Customer tải giỏ server và validate lại tồn mà không checkout', async () => {
  let validateCalled = false
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/cart`, () => HttpResponse.json(serverCart)),
    http.get(`${root}/account/addresses`, () => HttpResponse.json([{
      id: 'address-1', recipientName: 'Người nhận', phone: '0901234567', addressLine: '17 Đường Sân Cầu',
      ward: null, province: 'TP Hồ Chí Minh', isDefault: true, createdAt: '2026-10-30T00:00:00Z',
    }])),
    http.post(`${root}/cart/validate`, ({ request }) => {
      validateCalled = request.headers.get('X-CSRF-TOKEN') === 'csrf-test-token'
      return HttpResponse.json({ isValid: true, cart: serverCart, issues: [] })
    }),
  )
  const user = userEvent.setup()
  open('/cart')
  expect(await screen.findByText(/Kiểm tra sản phẩm, chọn địa chỉ/)).toBeVisible()
  expect(screen.getAllByText(/4\.800\.000/)).toHaveLength(2)
  await user.click(screen.getByRole('button', { name: 'Kiểm tra trước checkout' }))
  expect(await screen.findByRole('status')).toHaveTextContent('Giỏ hàng hợp lệ ở thời điểm kiểm tra')
  expect(validateCalled).toBe(true)
})

test('đăng nhập Customer tự gộp giỏ Guest bằng SKU và quantity rồi xóa local', async () => {
  addGuestItem({
    productSkuId: 'sku-racket', productId: 'product-racket', productTitle: 'Vợt High Pro', skuCode: 'RACKET-4U',
    optionLabel: '4U · G5', unitPrice: 2400000, imageUrl: null,
  }, 2)
  let authenticated = false
  let mergeBody: unknown
  server.use(
    http.get(`${root}/auth/me`, () => authenticated ? HttpResponse.json(customer) : HttpResponse.json({}, { status: 401 })),
    http.post(`${root}/auth/login`, () => { authenticated = true; return HttpResponse.json(customer) }),
    http.post(`${root}/cart/merge`, async ({ request }) => {
      mergeBody = await request.json()
      return HttpResponse.json({ cart: serverCart, issues: [] })
    }),
    http.get(`${root}/cart`, () => HttpResponse.json(serverCart)),
    http.get(`${root}/account/addresses`, () => HttpResponse.json([{
      id: 'address-1', recipientName: 'Người nhận', phone: '0901234567', addressLine: '17 Đường Sân Cầu',
      ward: null, province: 'TP Hồ Chí Minh', isDefault: true, createdAt: '2026-10-30T00:00:00Z',
    }])),
  )
  const user = userEvent.setup()
  const route = open('/login?returnTo=%2Fcart')
  await user.type(await screen.findByLabelText('Email'), customer.email)
  await user.type(screen.getByLabelText('Mật khẩu'), 'Valid password 123')
  await user.click(screen.getByRole('button', { name: 'Đăng nhập' }))
  await waitFor(() => expect(route.state.location.pathname).toBe('/cart'))
  expect(mergeBody).toEqual({ items: [{ productSkuId: 'sku-racket', quantity: 2, flashSaleOfferId: null }] })
  expect(localStorage.getItem('hcc.guest-cart.v1')).toBe('[]')
  expect(await screen.findByRole('status')).toHaveTextContent('Đã gộp giỏ Guest')
})
