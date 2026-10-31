import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'customer-1', email: 'customer@example.invalid', fullName: 'Khách thử nghiệm', role: 'CUSTOMER' }
const order = {
  id: '11111111-1111-1111-1111-111111111111', orderNumber: 'ORD-20261030-001',
  orderStatus: 'PENDING_COD_CONFIRMATION', paymentMethod: 'COD', paymentStatus: 'COD_PENDING',
  reservationStatus: 'HELD', totalAmount: 2500000, createdAt: '2026-10-30T02:00:00Z',
  deadlineAt: '2026-10-30T03:00:00Z', confirmedAt: null, closedReason: null,
  recipientName: 'Nguyễn Văn A', recipientPhone: '0901234567',
  shippingAddress: '12 Nguyễn Trãi, Quận 1, TP.HCM', shipmentId: null, shipmentStatus: null,
  shippedAt: null, deliveredAt: null, redeliveryCount: 0, canCancel: true, cancellationReason: null,
  items: [{ productSkuId: '22222222-2222-2222-2222-222222222222', productTitle: 'Vợt Yonex Astrox',
    skuCode: 'ASTROX-4U', optionLabel: '4U · Đỏ', quantity: 1, unitPrice: 2500000, lineTotal: 2500000 }],
  deliveryAttempts: [],
}
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

function open() {
  router = createMemoryRouter(appRoutes, { initialEntries: ['/orders'] })
  render(<RouterProvider router={router} />)
}

test('Customer xem danh sách, snapshot và chi tiết các trạng thái độc lập', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/orders`, ({ request }) => {
      const url = new URL(request.url)
      if (url.searchParams.get('page') !== '1' || url.searchParams.get('pageSize') !== '50') return HttpResponse.json({}, { status: 400 })
      return HttpResponse.json({ items: [order], page: 1, pageSize: 50, totalCount: 1 })
    }),
  )
  const user = userEvent.setup()
  open()

  expect(await screen.findByText(order.orderNumber)).toBeVisible()
  expect(screen.getByText('Vợt Yonex Astrox')).toBeVisible()
  expect(screen.getAllByText('Chờ xác nhận COD')).toHaveLength(2)
  expect(screen.getByText('Chờ thu COD')).toBeVisible()
  expect(screen.getByText('Đang giữ hàng')).toBeVisible()
  expect(screen.getByText('Chưa tạo')).toBeVisible()

  await user.click(screen.getByRole('button', { name: 'Xem chi tiết đơn' }))
  expect(screen.getByText('Nguyễn Văn A · 0901234567')).toBeVisible()
  expect(screen.getByText('12 Nguyễn Trãi, Quận 1, TP.HCM')).toBeVisible()
})

test('Customer hủy đơn COD bằng CSRF rồi giao diện phản ánh trạng thái hoàn kho', async () => {
  let body: unknown
  const cancelled = {
    ...order, orderStatus: 'CANCELLED', paymentStatus: 'VOID', reservationStatus: 'RELEASED',
    closedReason: 'CUSTOMER_CANCELLED', canCancel: false, cancellationReason: 'Đặt nhầm phiên bản vợt',
  }
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/orders`, () => HttpResponse.json({ items: [order], page: 1, pageSize: 50, totalCount: 1 })),
    http.post(`${root}/orders/${order.id}/cancel`, async ({ request }) => {
      body = await request.json()
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
      return HttpResponse.json(cancelled)
    }),
  )
  const user = userEvent.setup()
  open()

  await user.type(await screen.findByLabelText('Lý do hủy đơn COD'), 'Đặt nhầm phiên bản vợt')
  await user.click(screen.getByRole('button', { name: 'Hủy đơn' }))

  expect(await screen.findByRole('status')).toHaveTextContent('hàng giữ đã được hoàn lại kho')
  expect(body).toEqual({ reason: 'Đặt nhầm phiên bản vợt' })
  expect(screen.getAllByText('Đã hủy').length).toBeGreaterThan(0)
  expect(screen.getByText('Đã vô hiệu')).toBeVisible()
  expect(screen.getByText('Đã hoàn kho')).toBeVisible()
  await waitFor(() => expect(screen.queryByLabelText('Lý do hủy đơn COD')).toBeNull())
})
