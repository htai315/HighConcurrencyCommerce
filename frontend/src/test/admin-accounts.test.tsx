import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import { getAdminAccounts } from '../features/admin-accounts/admin-accounts.api'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => {
  router?.dispose()
})

const mockAdminMe = {
  id: 'admin-id-1',
  email: 'admin@example.com',
  fullName: 'Admin Quản Trị',
  role: 'ADMIN',
}

const mockAccountsList = [
  {
    id: 'admin-id-1',
    email: 'admin@example.com',
    fullName: 'Admin Quản Trị',
    role: 'ADMIN',
    accountStatus: 'ACTIVE',
    lifecycleStatus: 'ACTIVE',
    emailVerifiedAt: '2026-01-01T00:00:00Z',
    createdAt: '2026-01-01T00:00:00Z',
    activeSessionCount: 1,
    latestSessionAt: '2026-11-03T08:00:00Z',
  },
  {
    id: 'staff-id-1',
    email: 'staff.pending@example.com',
    fullName: 'Nhân Sự Chờ Duyệt',
    role: 'STAFF',
    accountStatus: 'ACTIVE',
    lifecycleStatus: 'INVITATION_PENDING',
    emailVerifiedAt: null,
    createdAt: '2026-11-02T10:00:00Z',
    activeSessionCount: 0,
    latestSessionAt: null,
  },
  {
    id: 'customer-id-1',
    email: 'customer@example.com',
    fullName: 'Khách Hàng Thân Thiết',
    role: 'CUSTOMER',
    accountStatus: 'ACTIVE',
    lifecycleStatus: 'ACTIVE',
    emailVerifiedAt: '2026-06-15T00:00:00Z',
    createdAt: '2026-06-15T00:00:00Z',
    activeSessionCount: 2,
    latestSessionAt: '2026-11-03T07:30:00Z',
  },
]

describe('Admin Accounts Management Frontend Tests', () => {
  test('từ chối response danh sách tài khoản sai contract thay vì ép kiểu mù', async () => {
    server.use(http.get(`${root}/admin/accounts`, () => HttpResponse.json({
      items: [{ ...mockAccountsList[0], role: 'SUPER_ADMIN' }],
      page: 1,
      pageSize: 15,
      totalCount: 1,
    })))

    await expect(getAdminAccounts()).rejects.toMatchObject({ status: 502, code: 'INVALID_RESPONSE' })
  })

  test('Admin mở tab Tài khoản & Nhân sự, xem danh sách và tự nhận diện tài khoản của mình', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, () =>
        HttpResponse.json({
          items: mockAccountsList,
          page: 1,
          pageSize: 15,
          totalCount: 3,
        })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    await userEvent.setup().click(accountsTab)

    expect(await screen.findByRole('heading', { name: /Tài khoản & Nhân sự \(3\)/i })).toBeVisible()
    expect(screen.getByText('admin@example.com')).toBeVisible()
    expect(screen.getByText('staff.pending@example.com')).toBeVisible()
    expect(screen.getByText('customer@example.com')).toBeVisible()

    // Self account should show indicator instead of action buttons
    expect(screen.getByText('(Tài khoản của bạn)')).toBeVisible()
    // Staff with pending invitation should have 'Gửi lại mời'
    expect(screen.getByRole('button', { name: 'Gửi lại mời' })).toBeVisible()
    // Customer with 2 sessions should have 'Thu hồi phiên'
    expect(screen.getByRole('button', { name: 'Thu hồi phiên' })).toBeVisible()
  })

  test('Admin tìm kiếm tài khoản theo từ khóa', async () => {
    let capturedSearchQuery = ''

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, ({ request }) => {
        const url = new URL(request.url)
        capturedSearchQuery = url.searchParams.get('q') || ''
        return HttpResponse.json({
          items: [mockAccountsList[1]],
          page: 1,
          pageSize: 15,
          totalCount: 1,
        })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    const user = userEvent.setup()
    await user.click(accountsTab)

    const searchInput = await screen.findByPlaceholderText('Tìm theo email, họ tên…')
    await waitFor(() => expect(searchInput).not.toBeDisabled())
    await user.type(searchInput, 'staff.pending')
    await user.click(screen.getByRole('button', { name: 'Lọc' }))

    await waitFor(() => {
      expect(capturedSearchQuery).toBe('staff.pending')
    })
  })

  test('Admin gửi lời mời nhân sự Staff mới qua form', async () => {
    let capturedBody: any = null
    let capturedCsrf: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, () =>
        HttpResponse.json({
          items: mockAccountsList,
          page: 1,
          pageSize: 15,
          totalCount: 3,
        })
      ),
      http.post(`${root}/admin/staff-invitations`, async ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        capturedBody = await request.json()
        return HttpResponse.json({
          id: 'staff-new-id',
          email: capturedBody.email,
          fullName: capturedBody.fullName,
          phone: null,
          role: 'STAFF',
          accountStatus: 'ACTIVE',
          lifecycleStatus: 'INVITATION_PENDING',
          emailVerifiedAt: null,
          lockedAt: null,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          activeSessionCount: 0,
          latestSessionAt: null,
          latestSessionExpiresAt: null,
          hasPassword: false,
          hasGoogleIdentity: false,
        }, { status: 201 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    const user = userEvent.setup()
    await user.click(accountsTab)

    const openInviteBtn = await screen.findByRole('button', { name: '＋ Mời nhân sự (Staff)' })
    await waitFor(() => expect(openInviteBtn).not.toBeDisabled())
    await user.click(openInviteBtn)

    expect(screen.getByRole('heading', { name: 'Mời nhân sự mới' })).toBeVisible()

    const emailInput = screen.getByPlaceholderText('staff@example.com')
    const nameInput = screen.getByPlaceholderText('Nguyễn Văn A')
    await user.type(emailInput, 'staff.recruit@example.com')
    await user.type(nameInput, 'Nguyễn Văn Tuyển Dụng')

    await user.click(screen.getByRole('button', { name: 'Gửi lời mời Staff' }))

    await waitFor(() => {
      expect(capturedCsrf).toBe('csrf-test-token')
      expect(capturedBody).toEqual({
        email: 'staff.recruit@example.com',
        fullName: 'Nguyễn Văn Tuyển Dụng',
      })
    })

    expect(await screen.findByRole('alert')).toHaveTextContent('Đã gửi lời mời tham gia nhân sự tới email: staff.recruit@example.com')
  })

  test('Admin gửi lại lời mời nhân sự với confirmation dialog', async () => {
    let resentTargetId: string | null = null
    let capturedCsrf: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, () =>
        HttpResponse.json({
          items: mockAccountsList,
          page: 1,
          pageSize: 15,
          totalCount: 3,
        })
      ),
      http.post(`${root}/admin/accounts/:id/staff-invitation/resend`, async ({ params, request }) => {
        resentTargetId = params.id as string
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        return new HttpResponse(null, { status: 204 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    const user = userEvent.setup()
    await user.click(accountsTab)

    const resendBtn = await screen.findByRole('button', { name: 'Gửi lại mời' })
    await waitFor(() => expect(resendBtn).not.toBeDisabled())
    await user.click(resendBtn)

    // Confirm dialog should open
    expect(screen.getByRole('heading', { name: 'Gửi lại lời mời tham gia?' })).toBeVisible()
    const confirmBtn = screen.getByRole('button', { name: 'Gửi lại lời mời' })
    await user.click(confirmBtn)

    await waitFor(() => {
      expect(resentTargetId).toBe('staff-id-1')
      expect(capturedCsrf).toBe('csrf-test-token')
    })

    expect(await screen.findByRole('alert')).toHaveTextContent('Đã gửi lại lời mời kích hoạt tới staff.pending@example.com')
  })

  test('Admin khóa tài khoản khách hàng', async () => {
    let statusTargetId: string | null = null
    let capturedBody: any = null
    let capturedCsrf: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, () =>
        HttpResponse.json({
          items: mockAccountsList,
          page: 1,
          pageSize: 15,
          totalCount: 3,
        })
      ),
      http.patch(`${root}/admin/accounts/:id/status`, async ({ params, request }) => {
        statusTargetId = params.id as string
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        capturedBody = await request.json()
        return new HttpResponse(null, { status: 204 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    const user = userEvent.setup()
    await user.click(accountsTab)

    const lockButtons = await screen.findAllByRole('button', { name: 'Khóa' })
    await waitFor(() => expect(lockButtons[1]!).not.toBeDisabled())
    // Click on Customer row's 'Khóa' button (second button after staff's)
    await user.click(lockButtons[1]!)

    expect(screen.getByRole('heading', { name: 'Khóa tài khoản này?' })).toBeVisible()
    const confirmBtn = screen.getByRole('button', { name: 'Xác nhận khóa' })
    await user.click(confirmBtn)

    await waitFor(() => {
      expect(statusTargetId).toBe('customer-id-1')
      expect(capturedCsrf).toBe('csrf-test-token')
      expect(capturedBody).toEqual({ status: 'LOCKED' })
    })

    expect(await screen.findByRole('alert')).toHaveTextContent('Đã khóa tài khoản customer@example.com')
  })

  test('Admin thu hồi toàn bộ phiên đăng nhập của người dùng', async () => {
    let revokeTargetId: string | null = null
    let capturedCsrf: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockAdminMe)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/accounts`, () =>
        HttpResponse.json({
          items: mockAccountsList,
          page: 1,
          pageSize: 15,
          totalCount: 3,
        })
      ),
      http.post(`${root}/admin/accounts/:id/sessions/revoke`, async ({ params, request }) => {
        revokeTargetId = params.id as string
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        return new HttpResponse(null, { status: 204 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    const accountsTab = await screen.findByRole('tab', { name: /Tài khoản & Nhân sự/i })
    const user = userEvent.setup()
    await user.click(accountsTab)

    const revokeBtn = await screen.findByRole('button', { name: 'Thu hồi phiên' })
    await waitFor(() => expect(revokeBtn).not.toBeDisabled())
    await user.click(revokeBtn)

    expect(screen.getByRole('heading', { name: 'Thu hồi toàn bộ phiên đăng nhập?' })).toBeVisible()
    const confirmBtn = screen.getByRole('button', { name: 'Thu hồi toàn bộ phiên' })
    await user.click(confirmBtn)

    await waitFor(() => {
      expect(revokeTargetId).toBe('customer-id-1')
      expect(capturedCsrf).toBe('csrf-test-token')
    })

    expect(await screen.findByRole('alert')).toHaveTextContent('Đã thu hồi toàn bộ phiên đăng nhập của customer@example.com')
  })
})
