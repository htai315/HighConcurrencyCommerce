import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'customer-payos', email: 'payos@example.invalid', fullName: 'Khách PayOS', role: 'CUSTOMER' }
const orderId = '11111111-2222-3333-4444-555555555555'

let router: ReturnType<typeof createMemoryRouter> | undefined

beforeEach(() => sessionStorage.setItem('hcc.payos-return-order.v1', orderId))
afterEach(() => {
  router?.dispose()
  sessionStorage.clear()
})

test('trang PayOS return chỉ hiển thị thành công sau khi backend xác minh SUCCESS', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/customer/orders/${orderId}/payment`, () => HttpResponse.json({
      orderId,
      orderNumber: 'HCC-PAYOS-001',
      orderStatus: 'CONFIRMED',
      paymentStatus: 'SUCCESS',
      expectedAmount: 1590000,
      deadlineAt: '2026-11-09T12:00:00Z',
      latestAttempt: {
        attemptId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
        attemptNo: 1,
        status: 'PAID',
        checkoutUrl: 'https://pay.payos.vn/web/test-safe-link',
        expiresAt: '2026-11-09T12:00:00Z',
        requiresProviderQuery: false,
      },
    })),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/payment/payos/return?status=CANCELLED'] })
  render(<RouterProvider router={router} />)

  expect(await screen.findByRole('heading', { name: 'Thanh toán thành công' })).toBeVisible()
  expect(screen.getByText('HCC-PAYOS-001')).toBeVisible()
  expect(screen.getByText('1.590.000 ₫')).toBeVisible()
  expect(screen.queryByRole('button', { name: 'Tiếp tục thanh toán' })).toBeNull()
})

test('trang PayOS return không tin query string khi backend vẫn PENDING', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/customer/orders/${orderId}/payment`, () => HttpResponse.json({
      orderId,
      orderNumber: 'HCC-PAYOS-002',
      orderStatus: 'PENDING_PAYMENT',
      paymentStatus: 'PENDING',
      expectedAmount: 850000,
      deadlineAt: '2026-11-09T12:00:00Z',
      latestAttempt: null,
    })),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/payment/payos/return?status=PAID'] })
  render(<RouterProvider router={router} />)

  expect(await screen.findByRole('heading', { name: 'Đang xác minh thanh toán' })).toBeVisible()
  expect(screen.getByRole('button', { name: 'Tiếp tục thanh toán' })).toBeVisible()
  expect(screen.queryByText('Thanh toán thành công')).toBeNull()
})

test('lỗi tải trạng thái ban đầu ở lại trang PayOS và cho phép thử lại', async () => {
  let requests = 0
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/customer/orders/${orderId}/payment`, () => {
      requests += 1
      if (requests === 1) return HttpResponse.json({ code: 'TEMPORARY_FAILURE' }, { status: 503 })
      return HttpResponse.json({
        orderId, orderNumber: 'HCC-PAYOS-RETRY', orderStatus: 'PENDING_PAYMENT', paymentStatus: 'PENDING',
        expectedAmount: 500000, deadlineAt: '2026-11-09T12:00:00Z', latestAttempt: null,
      })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/payment/payos/return'] })
  render(<RouterProvider router={router} />)

  expect(await screen.findByRole('heading', { name: 'Đang xác minh thanh toán' })).toBeVisible()
  expect(screen.getByRole('alert')).toHaveTextContent('Chưa thể xác minh thanh toán')
  expect(screen.queryByText('Chưa thể kiểm tra phiên đăng nhập.')).toBeNull()

  await userEvent.setup().click(screen.getByRole('button', { name: 'Kiểm tra lại' }))
  expect(await screen.findByText('HCC-PAYOS-RETRY')).toBeVisible()
  expect(screen.queryByRole('alert')).toBeNull()
})

test('khóa refresh PayOS trong khi request đang chạy để tránh polling chồng nhau', async () => {
  let requests = 0
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/customer/orders/${orderId}/payment`, async () => {
      requests += 1
      if (requests > 1) await gate
      return HttpResponse.json({
        orderId, orderNumber: 'HCC-PAYOS-LOCK', orderStatus: 'PENDING_PAYMENT', paymentStatus: 'PENDING',
        expectedAmount: 500000, deadlineAt: '2026-11-09T12:00:00Z', latestAttempt: null,
      })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/payment/payos/return'] })
  render(<RouterProvider router={router} />)
  await screen.findByText('HCC-PAYOS-LOCK')
  const button = screen.getByRole('button', { name: 'Kiểm tra lại' })
  await userEvent.setup().click(button)

  await waitFor(() => expect(button).toBeDisabled())
  expect(screen.getByRole('status')).toHaveTextContent('Đang kiểm tra với hệ thống')
  expect(requests).toBe(2)
  release()
  await waitFor(() => expect(button).not.toBeDisabled())
})
