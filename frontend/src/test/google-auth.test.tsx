import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { server } from './setup'
import { GoogleSignInButton } from '../features/auth/GoogleSignInButton'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { appRoutes } from '../App'

describe('Google Authentication Frontend', () => {
  let router: ReturnType<typeof createMemoryRouter> | undefined
  afterEach(() => router?.dispose())

  beforeEach(() => {
    vi.restoreAllMocks()
    window.google = {
      accounts: {
        id: {
          initialize: vi.fn(),
          renderButton: vi.fn((container: HTMLElement) => {
            const btn = document.createElement('button')
            btn.type = 'button'
            btn.textContent = 'Sign in with Google'
            btn.dataset.testid = 'mock-google-btn'
            container.appendChild(btn)
          }),
          cancel: vi.fn(),
        }
      }
    }
  })

  it.each(['STAFF', 'ADMIN'])('%s retains password form without requesting Customer auth methods', async role => {
    let methodCalls = 0
    server.use(
      http.get('http://localhost:5173/api/v1/auth/me', () => HttpResponse.json({ id: 'staff', email: 'staff@example.invalid', fullName: 'Operator', role })),
      http.get('http://localhost:5173/api/v1/account/auth-methods', () => { methodCalls++; return HttpResponse.json({}, { status: 403 }) }),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/security'] })
    render(<RouterProvider router={router} />)
    expect(await screen.findByLabelText('Mật khẩu hiện tại')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Liên kết Google' })).toBeNull()
    expect(methodCalls).toBe(0)
  })

  it('link loads SDK directly and typing password does not replace challenge', async () => {
    const sdk = window.google
    delete window.google
    let challengeCalls = 0
    let submittedPassword: unknown
    let callback: ((response: { credential: string }) => void) | undefined
    if (!sdk) throw new Error('Missing test SDK')
    sdk.accounts.id.initialize = vi.fn(config => { callback = config.callback })
    server.use(
      http.get('http://localhost:5173/api/v1/auth/me', () => HttpResponse.json({ id: 'u1', email: 'user@example.invalid', fullName: 'Customer', role: 'CUSTOMER' })),
      http.get('http://localhost:5173/api/v1/account/auth-methods', () => HttpResponse.json({ hasPassword: true, googleLinked: false, googleEmail: null })),
      http.get('http://localhost:5173/api/v1/auth/providers', () => HttpResponse.json({ google: { enabled: true, clientId: 'test' } })),
      http.post('http://localhost:5173/api/v1/auth/google/challenge', () => { challengeCalls++; return HttpResponse.json({ challengeId: 'link-1', nonce: 'nonce' }) }),
      http.post('http://localhost:5173/api/v1/account/auth-methods/google', async ({ request }) => { submittedPassword = await request.json(); return new HttpResponse(null, { status: 204 }) }),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/security'] })
    render(<RouterProvider router={router} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Liên kết Google' }))
    await waitFor(() => expect(document.querySelector('script[src="https://accounts.google.com/gsi/client"]')).not.toBeNull())
    window.google = sdk
    const script = document.querySelector('script[src="https://accounts.google.com/gsi/client"]')
    if (!script) throw new Error('SDK script missing')
    fireEvent.load(script)
    await waitFor(() => expect(callback).toBeDefined())
    await user.type(screen.getByLabelText('Xác nhận mật khẩu hiện tại'), 'CurrentPassword123!')
    expect(challengeCalls).toBe(1)
    expect(sdk.accounts.id.initialize).toHaveBeenCalledTimes(1)
    await act(async () => { await callback?.({ credential: 'token' }) })
    expect(submittedPassword).toEqual({ credential: 'token', challengeId: 'link-1', currentPassword: 'CurrentPassword123!' })
    script.remove()
  })

  it('failed login retries with a fresh challenge and ignores duplicate and unmounted callbacks', async () => {
    let callback: ((response: { credential: string }) => void) | undefined
    let challenges = 0
    let submissions = 0
    window.google!.accounts.id.initialize = vi.fn(config => { callback = config.callback })
    server.use(
      http.get('http://localhost:5173/api/v1/auth/providers', () => HttpResponse.json({ google: { enabled: true, clientId: 'test' } })),
      http.post('http://localhost:5173/api/v1/auth/google/challenge', () => HttpResponse.json({ challengeId: `challenge-${++challenges}`, nonce: 'nonce' })),
      http.post('http://localhost:5173/api/v1/auth/google', () => { submissions++; return HttpResponse.json({ code: 'GOOGLE_AUTH_UNAVAILABLE' }, { status: 503 }) }),
    )
    router = createMemoryRouter([{ path: '/login', element: <GoogleSignInButton mode="login" /> }], { initialEntries: ['/login'] })
    const view = render(<RouterProvider router={router} />)
    await waitFor(() => expect(callback).toBeDefined())
    const previous = callback
    await act(async () => { await Promise.all([callback?.({ credential: 'test' }), callback?.({ credential: 'test' })]) })
    expect(submissions).toBe(1)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Thử lại Google' }))
    await waitFor(() => expect(challenges).toBe(2))
    await waitFor(() => expect(callback).not.toBe(previous))
    view.unmount()
    await act(async () => { await callback?.({ credential: 'late' }) })
    expect(submissions).toBe(1)
  })

  it('expired challenge offers explicit renewal without background request loops', async () => {
    let callback: ((response: { credential: string }) => void) | undefined
    window.google!.accounts.id.initialize = vi.fn(config => { callback = config.callback })
    server.use(
      http.get('http://localhost:5173/api/v1/auth/providers', () => HttpResponse.json({ google: { enabled: true, clientId: 'test' } })),
      http.post('http://localhost:5173/api/v1/auth/google/challenge', () => HttpResponse.json({ challengeId: 'expiry', nonce: 'nonce' })),
    )
    const timer = vi.spyOn(window, 'setTimeout')
    router = createMemoryRouter([{ path: '/login', element: <GoogleSignInButton mode="login" /> }], { initialEntries: ['/login'] })
    render(<RouterProvider router={router} />)
    await waitFor(() => expect(callback).toBeDefined())
    const expiry = timer.mock.calls.find(call => call[1] === 240000)?.[0]
    expect(typeof expiry).toBe('function')
    act(() => { if (typeof expiry === 'function') expiry() })
    expect(screen.getByRole('button', { name: 'Thử lại Google' })).toBeVisible()
    expect(window.google!.accounts.id.initialize).toHaveBeenCalledTimes(1)
  })

  it('GoogleSignInButton không hiển thị khi Google provider bị tắt', async () => {
    server.use(
      http.get('http://localhost:5173/api/v1/auth/providers', () =>
        HttpResponse.json({ google: { enabled: false, clientId: '' } }))
    )

    router = createMemoryRouter([{ path: '/login', element: <GoogleSignInButton mode="login" /> }], { initialEntries: ['/login'] })
    const { container } = render(<RouterProvider router={router} />)

    await waitFor(() => {
      expect(container.querySelector('.google-auth-wrapper')).toBeNull()
    })
  })

  it('GoogleSignInButton hiển thị và khởi tạo GIS khi provider được bật', async () => {
    server.use(
      http.get('http://localhost:5173/api/v1/auth/providers', () =>
        HttpResponse.json({ google: { enabled: true, clientId: 'test-client-id.apps.googleusercontent.com' } })),
      http.post('http://localhost:5173/api/v1/auth/google/challenge', () =>
        HttpResponse.json({ challengeId: 'chal-123', nonce: 'nonce-abc' }))
    )

    router = createMemoryRouter([{ path: '/login', element: <GoogleSignInButton mode="login" /> }], { initialEntries: ['/login'] })
    render(<RouterProvider router={router} />)

    await waitFor(() => {
      expect(screen.getByText('Hoặc')).toBeInTheDocument()
      expect(screen.getByText(/Tiếp tục đồng nghĩa với việc bạn đồng ý/)).toBeInTheDocument()
    })

    expect(window.google!.accounts.id.initialize).toHaveBeenCalledWith(
      expect.objectContaining({
        client_id: 'test-client-id.apps.googleusercontent.com',
        nonce: 'nonce-abc',
      })
    )
  })

  it('GoogleSignInButton hiển thị thông báo hướng dẫn khi nhận lỗi ACCOUNT_LINK_REQUIRED', async () => {
    let capturedCallback: ((response: { credential: string }) => void) | null = null
    window.google!.accounts.id.initialize = vi.fn((config) => {
      capturedCallback = config.callback
    })

    server.use(
      http.get('http://localhost:5173/api/v1/auth/providers', () =>
        HttpResponse.json({ google: { enabled: true, clientId: 'test-client-id.apps.googleusercontent.com' } })),
      http.post('http://localhost:5173/api/v1/auth/google/challenge', () =>
        HttpResponse.json({ challengeId: 'chal-123', nonce: 'nonce-abc' })),
      http.post('http://localhost:5173/api/v1/auth/google', () =>
        HttpResponse.json({ code: 'ACCOUNT_LINK_REQUIRED', title: 'Account link required' }, { status: 409 }))
    )

    const onError = vi.fn()
    router = createMemoryRouter([{ path: '/login', element: <GoogleSignInButton mode="login" onError={onError} /> }], { initialEntries: ['/login'] })
    render(<RouterProvider router={router} />)

    await waitFor(() => {
      expect(capturedCallback).not.toBeNull()
    })

    // Trigger GIS callback
    await capturedCallback!({ credential: 'fake-jwt-token' })

    await waitFor(() => {
      expect(onError).toHaveBeenCalledWith(
        expect.stringContaining('Email này đã được đăng ký bằng mật khẩu. Vui lòng đăng nhập bằng mật khẩu rồi liên kết Google trong mục Bảo mật tài khoản.')
      )
    })
  })

  it('PasswordPage hiển thị trạng thái phương thức đăng nhập và ẩn form đổi mật khẩu với Google-only user', async () => {
    server.use(
      http.get('http://localhost:5173/api/v1/auth/me', () =>
        HttpResponse.json({ id: 'u1', email: 'googleuser@gmail.com', fullName: 'Google User', role: 'CUSTOMER' })),
      http.get('http://localhost:5173/api/v1/account/auth-methods', () =>
        HttpResponse.json({
          hasPassword: false,
          googleLinked: true,
          googleEmail: 'googleuser@gmail.com'
        }))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/security'] })
    render(<RouterProvider router={router} />)

    // Wait for auth methods
    await waitFor(() => {
      expect(screen.getByText('Phương thức đăng nhập')).toBeInTheDocument()
      expect(screen.getByText('Chưa cài đặt')).toBeInTheDocument()
      expect(screen.getByText('Đã liên kết')).toBeInTheDocument()
      expect(screen.getByText(/googleuser@gmail.com/)).toBeInTheDocument()
    })

    // Change password form must be hidden, with informational note displayed
    expect(screen.queryByLabelText('Mật khẩu hiện tại')).toBeNull()
    expect(screen.getByText(/Tài khoản của bạn hiện tại đăng nhập thông qua Google/)).toBeInTheDocument()

    // Unlink button must be disabled because user has no password
    const unlinkBtn = screen.getByRole('button', { name: 'Hủy liên kết' })
    expect(unlinkBtn).toBeDisabled()
  })

  it('PasswordPage cho phép mở form hủy liên kết khi tài khoản đã có mật khẩu', async () => {
    const user = userEvent.setup()
    server.use(
      http.get('http://localhost:5173/api/v1/auth/me', () =>
        HttpResponse.json({ id: 'u1', email: 'user@example.com', fullName: 'Customer', role: 'CUSTOMER' })),
      http.get('http://localhost:5173/api/v1/account/auth-methods', () =>
        HttpResponse.json({
          hasPassword: true,
          googleLinked: true,
          googleEmail: 'user@example.com'
        })),
      http.delete('http://localhost:5173/api/v1/account/auth-methods/google', () =>
        new HttpResponse(null, { status: 204 }))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/security'] })
    render(<RouterProvider router={router} />)

    await waitFor(() => {
      expect(screen.getByText('Đã cài đặt')).toBeInTheDocument()
      expect(screen.getByText('Đã liên kết')).toBeInTheDocument()
    })

    // Unlink button is enabled
    const unlinkBtn = screen.getByRole('button', { name: 'Hủy liên kết' })
    expect(unlinkBtn).not.toBeDisabled()

    // Click opens modal
    await user.click(unlinkBtn)
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('Hủy liên kết Google')).toBeInTheDocument()
  })
})
