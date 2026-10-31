import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'customer-notification', email: 'customer@example.invalid', fullName: 'Nguyễn Cầu Lông', role: 'CUSTOMER' }
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Customer chỉ xem feed của mình và đánh dấu thông báo đã đọc với CSRF', async () => {
  let readAt: string | null = null
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/notifications`, () => HttpResponse.json({
      unreadCount: readAt ? 0 : 1,
      items: [{ id: 'notification-1', type: 'ORDER_SHIPPING', title: 'Đơn hàng đang được giao',
        body: 'Đơn HC-001 đang trên đường giao đến bạn.', createdAt: '2026-10-30T08:00:00Z', readAt }],
    })),
    http.post(`${root}/notifications/:id/read`, ({ request, params }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token' || params.id !== 'notification-1')
        return HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })
      readAt = '2026-10-30T09:00:00Z'
      return HttpResponse.json({ id: 'notification-1', type: 'ORDER_SHIPPING', title: 'Đơn hàng đang được giao',
        body: 'Đơn HC-001 đang trên đường giao đến bạn.', createdAt: '2026-10-30T08:00:00Z', readAt })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/notifications'] })
  render(<RouterProvider router={router} />)
  expect(await screen.findByRole('heading', { name: 'Thông báo của bạn' })).toBeVisible()
  expect(screen.getByText('1 chưa đọc')).toBeVisible()
  expect(screen.getByText('Đơn HC-001 đang trên đường giao đến bạn.')).toBeVisible()
  await userEvent.setup().click(screen.getByRole('button', { name: 'Đánh dấu đã đọc' }))
  expect(await screen.findByText('Đã đọc')).toBeVisible()
  expect(screen.getByText('0 chưa đọc')).toBeVisible()
  expect(readAt).not.toBeNull()
})
