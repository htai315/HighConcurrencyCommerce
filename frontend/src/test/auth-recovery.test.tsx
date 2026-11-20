import { describe, it, expect, afterEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { appRoutes } from '../App'
import { server } from './setup'

const api = 'http://localhost:5173/api/v1/auth'

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => {
  router?.dispose()
  window.location.hash = ''
})

function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

describe('Auth Recovery Frontend Tests', () => {
  it('ForgotPasswordPage sends email and shows neutral notice', async () => {
    let capturedBody: any = null
    let capturedCsrf: string | null = null

    server.use(
      http.post(`${api}/password-reset/request`, async ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        capturedBody = await request.json()
        return HttpResponse.json({ message: 'Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.' }, { status: 202 })
      })
    )

    const user = userEvent.setup()
    open('/forgot-password')

    expect(await screen.findByRole('heading', { name: 'Quên mật khẩu' })).toBeInTheDocument()

    await user.type(screen.getByLabelText('Email'), 'user@example.com')
    await user.click(screen.getByRole('button', { name: 'Gửi liên kết đặt lại mật khẩu' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Nếu thông tin phù hợp')
    expect(capturedCsrf).toBe('csrf-test-token')
    expect(capturedBody).toEqual({ email: 'user@example.com' })
  })

  it('ResetPasswordPage reads token from hash, clears hash, and submits new password', async () => {
    let capturedBody: any = null
    window.location.hash = '#token=test-reset-token-123'

    server.use(
      http.post(`${api}/password-reset/confirm`, async ({ request }) => {
        capturedBody = await request.json()
        return new HttpResponse(null, { status: 204 })
      })
    )

    const user = userEvent.setup()
    const route = open('/reset-password')

    expect(await screen.findByRole('heading', { name: 'Đặt lại mật khẩu' })).toBeInTheDocument()
    // Hash should be cleared
    await waitFor(() => {
      expect(window.location.hash).toBe('')
    })

    await user.type(screen.getByLabelText('Mật khẩu mới', { exact: true }), 'NewStrongPass123!@#')
    await user.type(screen.getByLabelText('Nhập lại mật khẩu mới'), 'NewStrongPass123!@#')

    await user.click(screen.getByRole('button', { name: 'Lưu mật khẩu mới' }))

    await waitFor(() => {
      expect(route.state.location.pathname).toBe('/login')
      expect(route.state.location.search).toBe('?reset=1')
    })

    expect(capturedBody).toEqual({
      token: 'test-reset-token-123',
      newPassword: 'NewStrongPass123!@#',
      confirmPassword: 'NewStrongPass123!@#'
    })
  })

  it('ResetPasswordPage shows error when token is missing in hash', async () => {
    window.location.hash = ''
    open('/reset-password')

    expect(await screen.findByRole('alert')).toHaveTextContent('Liên kết đặt lại mật khẩu không hợp lệ')
  })

  it('VerifyEmailPage auto-verifies token from hash and shows success', async () => {
    let capturedToken: string | null = null
    window.location.hash = '#token=verify-token-xyz'

    server.use(
      http.post(`${api}/email-verification/confirm`, async ({ request }) => {
        const body: any = await request.json()
        capturedToken = body.token
        return new HttpResponse(null, { status: 204 })
      })
    )

    open('/verify-email')

    expect(await screen.findByRole('heading', { name: 'Xác minh thành công!' })).toBeInTheDocument()
    expect(capturedToken).toBe('verify-token-xyz')
    expect(window.location.hash).toBe('')
  })

  it('VerifyEmailPage shows resend form on verification failure and allows resending', async () => {
    window.location.hash = '#token=expired-token'
    let resendCapturedBody: any = null

    server.use(
      http.post(`${api}/email-verification/confirm`, () => {
        return HttpResponse.json({ code: 'INVALID_OR_EXPIRED_TOKEN', title: 'Liên kết không hợp lệ hoặc đã hết hạn.' }, { status: 400 })
      }),
      http.post(`${api}/email-verification/resend`, async ({ request }) => {
        resendCapturedBody = await request.json()
        return HttpResponse.json({ message: 'Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.' }, { status: 202 })
      })
    )

    const user = userEvent.setup()
    open('/verify-email')

    expect(await screen.findByRole('heading', { name: 'Xác minh không thành công' })).toBeInTheDocument()

    await user.type(screen.getByLabelText('Email'), 'myemail@example.com')
    await user.click(screen.getByRole('button', { name: 'Gửi lại email xác minh' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Nếu thông tin phù hợp')
    expect(resendCapturedBody).toEqual({ email: 'myemail@example.com' })
  })

  it('LoginPage shows EMAIL_NOT_VERIFIED alert with CTA to resend verification', async () => {
    server.use(
      http.post(`${api}/login`, () => {
        return HttpResponse.json({
          code: 'EMAIL_NOT_VERIFIED',
          title: 'Email chưa được xác minh. Vui lòng kiểm tra hộp thư hoặc yêu cầu gửi lại email xác minh.'
        }, { status: 403 })
      })
    )

    const user = userEvent.setup()
    open('/login')

    await user.type(await screen.findByLabelText('Email'), 'unverified@example.com')
    await user.type(screen.getByLabelText('Mật khẩu'), 'ValidPassword123!')
    await user.click(screen.getByRole('button', { name: 'Đăng nhập' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Email chưa được xác minh')
    expect(screen.getByRole('link', { name: 'Gửi lại email xác minh' })).toHaveAttribute(
      'href',
      '/verify-email?resend=1&email=unverified%40example.com'
    )
  })
})
