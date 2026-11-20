import { render, screen, waitFor, within } from '@testing-library/react'
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

test('Checkbox chưa đọc và nút Tải thêm gọi đúng API server với unreadOnly và before', async () => {
  let requestedUnreadOnly: string | null = null
  let requestedBefore: string | null = null

  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/notifications`, ({ request }) => {
      const url = new URL(request.url)
      requestedUnreadOnly = url.searchParams.get('unreadOnly')
      requestedBefore = url.searchParams.get('before')

      if (requestedBefore) {
        return HttpResponse.json({
          unreadCount: 1,
          hasMore: false,
          items: [{
            id: 'notification-page-2',
            type: 'ORDER_CONFIRMED',
            title: 'Thông báo trang 2',
            body: 'Nội dung trang 2',
            createdAt: '2026-10-30T07:00:00Z',
            readAt: null,
          }],
        })
      }

      if (requestedUnreadOnly === 'true') {
        return HttpResponse.json({
          unreadCount: 1,
          hasMore: false,
          items: [{
            id: 'notification-unread-only',
            type: 'ORDER_CONFIRMED',
            title: 'Thông báo chưa đọc từ server',
            body: 'Nội dung chưa đọc',
            createdAt: '2026-10-30T07:30:00Z',
            readAt: null,
          }],
        })
      }

      return HttpResponse.json({
        unreadCount: 2,
        hasMore: true,
        items: [{
          id: 'notification-page-1',
          type: 'ORDER_SHIPPING',
          title: 'Thông báo trang 1',
          body: 'Nội dung trang 1',
          createdAt: '2026-10-30T08:00:00Z',
          readAt: '2026-10-30T08:30:00Z',
        }],
      })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/notifications'] })
  render(<RouterProvider router={router} />)
  expect(await screen.findByText('Thông báo trang 1')).toBeVisible()

  // Click "Tải thêm"
  const loadMoreBtn = screen.getByRole('button', { name: 'Tải thêm thông báo' })
  expect(loadMoreBtn).toBeVisible()
  await userEvent.setup().click(loadMoreBtn)

  expect(await screen.findByText('Thông báo trang 2')).toBeVisible()
  expect(requestedBefore).toBe('2026-10-30T08:00:00Z')

  // Check "Chỉ hiện thông báo chưa đọc"
  const checkbox = screen.getByLabelText(/Chỉ hiện thông báo chưa đọc/i)
  await userEvent.setup().click(checkbox)

  expect(await screen.findByText('Thông báo chưa đọc từ server')).toBeVisible()
  expect(requestedUnreadOnly).toBe('true')
})

test('Khi đang lọc chỉ chưa đọc, đánh dấu đã đọc loại bỏ thông báo khỏi danh sách', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/notifications`, () => {
      return HttpResponse.json({
        unreadCount: 1,
        hasMore: false,
        items: [{
          id: 'notif-unread-1',
          type: 'ORDER_CONFIRMED',
          title: 'Thông báo cần xử lý',
          body: 'Chi tiết thông báo',
          createdAt: '2026-10-30T10:00:00Z',
          readAt: null,
        }],
      })
    }),
    http.post(`${root}/notifications/:id/read`, ({ params }) => {
      return HttpResponse.json({
        id: params.id,
        type: 'ORDER_CONFIRMED',
        title: 'Thông báo cần xử lý',
        body: 'Chi tiết thông báo',
        createdAt: '2026-10-30T10:00:00Z',
        readAt: '2026-10-30T10:05:00Z',
      })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/notifications'] })
  render(<RouterProvider router={router} />)

  expect(await screen.findByText('Thông báo cần xử lý')).toBeVisible()
  expect(screen.getByText('1 chưa đọc')).toBeVisible()

  // Bật filter "Chỉ hiện thông báo chưa đọc trong danh sách"
  const checkbox = screen.getByLabelText(/Chỉ hiện thông báo chưa đọc/i)
  await userEvent.setup().click(checkbox)

  // Bấm Đánh dấu đã đọc
  const readBtn = await screen.findByRole('button', { name: 'Đánh dấu đã đọc' })
  await userEvent.setup().click(readBtn)

  // Thông báo bị loại khỏi danh sách
  await waitFor(() => {
    expect(screen.queryByText('Thông báo cần xử lý')).toBeNull()
  })
  expect(screen.getByText('0 chưa đọc')).toBeVisible()
  expect(screen.getByText('Chưa có thông báo')).toBeVisible()
})

test('Cảnh báo Wishlist hiển thị loại và chỉ chấp nhận action URL nội bộ an toàn', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/notifications`, () => HttpResponse.json({
      unreadCount: 2,
      hasMore: false,
      items: [
        {
          id: 'wishlist-safe',
          type: 'WISHLIST_BACK_IN_STOCK',
          title: 'Vợt đã có hàng',
          body: 'Sản phẩm bạn lưu đã có hàng trở lại.',
          actionUrl: '/products/11111111-1111-1111-1111-111111111111',
          createdAt: '2026-11-07T10:00:00Z',
          readAt: null,
        },
        {
          id: 'wishlist-unsafe',
          type: 'WISHLIST_PRICE_DROP',
          title: 'Giá đã giảm',
          body: 'Giá thường vừa giảm.',
          actionUrl: 'https://evil.example/phishing',
          createdAt: '2026-11-07T09:00:00Z',
          readAt: null,
        },
      ],
    })),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/notifications'] })
  render(<RouterProvider router={router} />)
  const safeCard = (await screen.findByText('Vợt đã có hàng')).closest('article')!
  expect(within(safeCard).getByText('Có hàng trở lại')).toBeVisible()
  expect(within(safeCard).getByRole('link', { name: /Xem sản phẩm/i })).toHaveAttribute(
    'href', '/products/11111111-1111-1111-1111-111111111111')
  const unsafeCard = screen.getByText('Giá đã giảm').closest('article')!
  expect(within(unsafeCard).getByText('Giảm giá sản phẩm yêu thích')).toBeVisible()
  expect(within(unsafeCard).queryByRole('link', { name: /Xem sản phẩm/i })).toBeNull()
})
