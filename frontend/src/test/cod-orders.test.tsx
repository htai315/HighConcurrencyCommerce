import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const staff = { id: 'staff-cod', email: 'staff@example.invalid', fullName: 'Staff COD', role: 'STAFF' }
const pending = {
  orderId: 'order-cod-1', orderNumber: 'HC-20261030-COD001', orderStatus: 'PENDING_COD_CONFIRMATION', paymentStatus: 'COD_PENDING',
  customerId: 'customer-1', customerName: 'Nguyễn Cầu Lông', customerEmail: 'customer@example.invalid', totalAmount: 700000,
  createdAt: '2026-10-30T00:00:00Z', deadlineAt: '2026-10-31T00:00:00Z', recipientName: 'Người nhận',
  recipientPhone: '0901234567', shippingAddress: '18 Đường Cầu, Hà Nội',
  items: [{ productSkuId: 'sku-1', productTitle: 'Vợt High Pro', skuCode: 'HIGH-4U', optionLabel: '4U G5', quantity: 2, unitPrice: 350000, lineTotal: 700000 }],
}

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Staff xem hàng đợi và xác nhận COD với CSRF', async () => {
  let confirmed = false
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
    http.get(`${root}/staff/products`, () => HttpResponse.json([])),
    http.get(`${root}/staff/orders/cod-pending`, () => HttpResponse.json(confirmed ? [] : [pending])),
    http.get(`${root}/staff/shipments`, () => HttpResponse.json([])),
    http.post(`${root}/staff/orders/:id/confirm-cod`, ({ request, params }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token' || params.id !== pending.orderId)
        return HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })
      confirmed = true
      return HttpResponse.json({ ...pending, orderStatus: 'CONFIRMED' })
    }),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Đơn COD chờ xác nhận/ }))
  expect(await screen.findByText(pending.orderNumber)).toBeVisible()
  expect(screen.getByText('18 Đường Cầu, Hà Nội')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Xác nhận đơn COD' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('tồn giữ đã chuyển sang phân bổ')
  expect(screen.queryByText(pending.orderNumber)).toBeNull()
  expect(confirmed).toBe(true)
})
