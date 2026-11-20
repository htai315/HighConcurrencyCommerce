import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => {
  router?.dispose()
  window.location.hash = ''
})

describe('Staff Invitation Frontend Tests', () => {
  test('Staff đọc token từ URL hash, xóa hash, nhập mật khẩu hợp lệ và gửi kích hoạt thành công', async () => {
    let capturedBody: any = null
    let capturedCsrf: string | null = null
    window.location.hash = '#token=test-staff-token-12345'

    server.use(
      http.post(`${root}/auth/staff-invitation/accept`, async ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        capturedBody = await request.json()
        return new HttpResponse(null, { status: 204 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff-invitation'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: 'Kích hoạt tài khoản nhân sự' })).toBeVisible()
    // Verify hash has been cleared from URL
    await waitFor(() => {
      expect(window.location.hash).toBe('')
    })

    const passwordInput = screen.getByLabelText('Mật khẩu mới', { exact: true })
    const confirmInput = screen.getByLabelText('Nhập lại mật khẩu', { exact: true })
    const submitBtn = screen.getByRole('button', { name: 'Kích hoạt tài khoản' })

    const user = userEvent.setup()
    await user.type(passwordInput, 'StaffPassword2026!')
    await user.type(confirmInput, 'StaffPassword2026!')

    expect(submitBtn).not.toBeDisabled()
    await user.click(submitBtn)

    await waitFor(() => {
      expect(capturedCsrf).toBe('csrf-test-token')
      expect(capturedBody).toEqual({
        token: 'test-staff-token-12345',
        password: 'StaffPassword2026!',
        confirmPassword: 'StaffPassword2026!',
      })
    })

    // Should redirect to /login?staffInvited=1
    await waitFor(() => {
      expect(router?.state.location.pathname).toBe('/login')
      expect(router?.state.location.search).toBe('?staffInvited=1')
    })

    // Verify LoginPage renders the success notice
    expect(
      await screen.findByText('Tài khoản nhân sự đã kích hoạt thành công. Vui lòng đăng nhập bằng mật khẩu vừa tạo.')
    ).toBeVisible()
  })

  test('Hiển thị thông báo lỗi khi URL không có token trong hash', async () => {
    window.location.hash = ''

    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff-invitation'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Liên kết kích hoạt không hợp lệ hoặc thiếu mã bảo mật.'
    )
    expect(screen.getByRole('link', { name: 'Trở về trang đăng nhập' })).toBeVisible()
  })

  test('Hiển thị thông báo lỗi từ server khi token không hợp lệ hoặc đã hết hạn', async () => {
    window.location.hash = '#token=expired-token-xyz'

    server.use(
      http.post(`${root}/auth/staff-invitation/accept`, () =>
        HttpResponse.json({ code: 'INVALID_OR_EXPIRED_TOKEN', message: 'Liên kết mời không hợp lệ hoặc đã hết hạn.' }, { status: 400 })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff-invitation'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: 'Kích hoạt tài khoản nhân sự' })).toBeVisible()

    const user = userEvent.setup()
    await user.type(screen.getByLabelText('Mật khẩu mới', { exact: true }), 'StaffPassword2026!')
    await user.type(screen.getByLabelText('Nhập lại mật khẩu', { exact: true }), 'StaffPassword2026!')
    await user.click(screen.getByRole('button', { name: 'Kích hoạt tài khoản' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Liên kết mời không hợp lệ hoặc đã hết hạn.'
    )
  })
})
