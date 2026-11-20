import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import { WishlistProvider } from '../features/wishlist/WishlistContext'
import { useWishlist } from '../features/wishlist/useWishlist'
import type { AuthUser } from '../features/auth/auth.api'

const root = 'http://localhost:5173/api/v1'
const customer = { id: 'alert-customer', email: 'alert@example.invalid', fullName: 'Alert Customer', role: 'CUSTOMER', emailVerified: true }
const productId = '11111111-1111-1111-1111-111111111111'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => router?.dispose())

function alertSummary(revision = 0) {
  return { productId, backInStockEnabled: false, priceDropEnabled: false, emailEnabled: false, revision }
}

function alertSettings(overrides: Record<string, unknown> = {}) {
  return {
    ...alertSummary(),
    currentAvailability: 'OUT_OF_STOCK',
    currentMinPrice: 1800000,
    updatedAt: '2026-11-07T10:00:00Z',
    ...overrides,
  }
}

function wishlistPage() {
  return {
    items: [{
      productId,
      addedAt: '2026-11-07T09:00:00Z',
      availability: 'OUT_OF_STOCK',
      product: {
        id: productId,
        title: 'Vợt Yonex Alert Pro',
        category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
        minPrice: 1800000,
        inStock: false,
        imageUrl: null,
        averageRating: 4.8,
        reviewCount: 10,
      },
      alerts: alertSummary(),
    }],
    page: 1,
    pageSize: 12,
    totalCount: 1,
  }
}

function baseHandlers(authUser: Record<string, unknown> = customer) {
  return [
    http.get(`${root}/auth/me`, () => HttpResponse.json(authUser)),
    http.get(`${root}/wishlist`, () => HttpResponse.json(wishlistPage())),
    http.get(`${root}/wishlist/summary`, () => HttpResponse.json({
      productIds: [productId], totalCount: 1, alerts: [alertSummary()],
    })),
  ]
}

describe('T51.5 Wishlist product alert UI', () => {
  test('lưu hai loại cảnh báo và email bằng CSRF, cập nhật shared state', async () => {
    let requestBody: unknown
    let csrfHeader: string | null = null
    server.use(
      ...baseHandlers(),
      http.get(`${root}/wishlist/items/:id/alerts`, () => HttpResponse.json(alertSettings())),
      http.put(`${root}/wishlist/items/:id/alerts`, async ({ request }) => {
        requestBody = await request.json()
        csrfHeader = request.headers.get('X-CSRF-TOKEN')
        return HttpResponse.json(alertSettings({
          backInStockEnabled: true,
          priceDropEnabled: true,
          emailEnabled: true,
          revision: 1,
        }))
      }),
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)
    const trigger = await screen.findByTestId(`wishlist-alert-trigger-${productId}`)
    await userEvent.setup().click(trigger)
    const dialog = await screen.findByRole('dialog', { name: 'Nhận thông báo' })
    expect(await within(dialog).findByText('Giá thường hiện tại:')).toBeVisible()
    await userEvent.setup().click(within(dialog).getByLabelText(/Có hàng trở lại/i))
    await userEvent.setup().click(within(dialog).getByLabelText(/Giá thường giảm/i))
    await userEvent.setup().click(within(dialog).getByLabelText(/Gửi thêm qua email/i))
    await userEvent.setup().click(within(dialog).getByRole('button', { name: 'Lưu cảnh báo' }))

    expect(await within(dialog).findByText('Đã lưu lựa chọn cảnh báo.')).toBeVisible()
    expect(csrfHeader).toBe('csrf-test-token')
    expect(requestBody).toEqual({
      backInStockEnabled: true,
      priceDropEnabled: true,
      emailEnabled: true,
      expectedRevision: 0,
    })
    await userEvent.setup().click(within(dialog).getByRole('button', { name: 'Đóng' }))
    expect(trigger).toHaveTextContent('Đang theo dõi')
  })

  test('stale revision tải lại cấu hình mới nhất và thông báo rõ ràng', async () => {
    let getCount = 0
    server.use(
      ...baseHandlers(),
      http.get(`${root}/wishlist/items/:id/alerts`, () => {
        getCount += 1
        return HttpResponse.json(getCount === 1
          ? alertSettings({ revision: 2 })
          : alertSettings({ backInStockEnabled: true, emailEnabled: true, revision: 3 }))
      }),
      http.put(`${root}/wishlist/items/:id/alerts`, () => HttpResponse.json({
        code: 'STALE_WISHLIST_ALERT_SETTINGS', currentRevision: 3,
      }, { status: 409 })),
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)
    await userEvent.setup().click(await screen.findByTestId(`wishlist-alert-trigger-${productId}`))
    const dialog = await screen.findByRole('dialog', { name: 'Nhận thông báo' })
    await waitFor(() => expect(within(dialog).getByLabelText(/Giá thường giảm/i)).toBeEnabled())
    await userEvent.setup().click(within(dialog).getByLabelText(/Giá thường giảm/i))
    await userEvent.setup().click(within(dialog).getByRole('button', { name: 'Lưu cảnh báo' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Dữ liệu mới nhất đã được tải lại')
    expect(within(dialog).getByLabelText(/Có hàng trở lại/i)).toBeChecked()
    expect(within(dialog).getByLabelText(/Gửi thêm qua email/i)).toBeChecked()
    expect(getCount).toBe(2)
  })

  test('email chưa xác minh bị tắt lại và dialog trả focus về trigger khi Escape', async () => {
    server.use(
      ...baseHandlers(),
      http.get(`${root}/wishlist/items/:id/alerts`, () => HttpResponse.json(alertSettings())),
      http.put(`${root}/wishlist/items/:id/alerts`, () => HttpResponse.json({
        code: 'EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL',
      }, { status: 400 })),
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)
    const trigger = await screen.findByTestId(`wishlist-alert-trigger-${productId}`)
    await userEvent.setup().click(trigger)
    const dialog = await screen.findByRole('dialog', { name: 'Nhận thông báo' })
    await waitFor(() => expect(within(dialog).getByLabelText(/Có hàng trở lại/i)).toBeEnabled())
    await userEvent.setup().click(within(dialog).getByLabelText(/Có hàng trở lại/i))
    await userEvent.setup().click(within(dialog).getByLabelText(/Gửi thêm qua email/i))
    await userEvent.setup().click(within(dialog).getByRole('button', { name: 'Lưu cảnh báo' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('xác minh email')
    expect(within(dialog).getByLabelText(/Gửi thêm qua email/i)).not.toBeChecked()
    await userEvent.setup().keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: 'Nhận thông báo' })).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  test('session chưa xác minh email khóa kênh email trước khi mutation', async () => {
    server.use(
      ...baseHandlers({ ...customer, emailVerified: false }),
      http.get(`${root}/wishlist/items/:id/alerts`, () => HttpResponse.json(alertSettings())),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)
    await userEvent.setup().click(await screen.findByTestId(`wishlist-alert-trigger-${productId}`))
    const dialog = await screen.findByRole('dialog', { name: 'Nhận thông báo' })
    await waitFor(() => expect(within(dialog).getByLabelText(/Có hàng trở lại/i)).toBeEnabled())
    await userEvent.setup().click(within(dialog).getByLabelText(/Có hàng trở lại/i))
    expect(within(dialog).getByLabelText(/Gửi thêm qua email/i)).toBeDisabled()
    expect(within(dialog).getByText(/cần xác minh email trước/i)).toBeVisible()
  })

  test('response alert cũ không ghi đè shared state sau khi đổi tài khoản', async () => {
    let summaryCall = 0
    let releaseOld!: () => void
    const oldGate = new Promise<void>(resolve => { releaseOld = resolve })
    server.use(
      http.get(`${root}/wishlist/summary`, () => {
        summaryCall += 1
        return HttpResponse.json({
          productIds: [productId],
          totalCount: 1,
          alerts: [summaryCall === 1
            ? alertSummary(0)
            : { ...alertSummary(5), priceDropEnabled: true }],
        })
      }),
      http.get(`${root}/wishlist/items/:id/alerts`, async () => {
        await oldGate
        return HttpResponse.json(alertSettings({ backInStockEnabled: true, revision: 1 }))
      }),
    )
    const accountA: AuthUser = { ...customer, role: 'CUSTOMER', emailVerified: true }
    const accountB: AuthUser = { ...accountA, id: 'alert-customer-b', email: 'alert-b@example.invalid' }

    function Probe() {
      const wishlist = useWishlist()
      const alert = wishlist.getAlertSummary(productId)
      return <>
        <output data-testid="alert-probe">{alert?.revision}:{String(alert?.backInStockEnabled)}:{String(alert?.priceDropEnabled)}</output>
        <button type="button" onClick={() => void wishlist.loadAlertSettings(productId)}>Tải chi tiết</button>
      </>
    }

    const view = render(<MemoryRouter><WishlistProvider user={accountA}><Probe /></WishlistProvider></MemoryRouter>)
    await waitFor(() => expect(screen.getByTestId('alert-probe')).toHaveTextContent('0:false:false'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'Tải chi tiết' }))
    view.rerender(<MemoryRouter><WishlistProvider user={accountB}><Probe /></WishlistProvider></MemoryRouter>)
    await waitFor(() => expect(screen.getByTestId('alert-probe')).toHaveTextContent('5:false:true'))
    releaseOld()
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(screen.getByTestId('alert-probe')).toHaveTextContent('5:false:true')
  })
})
