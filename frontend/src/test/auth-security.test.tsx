import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import { safeReturnTo } from '../features/auth/auth.api'

test.each(['https://evil.example', '//evil.example', '/\\evil.example', '/%0d%0aevil'])('returnTo không chuyển sang origin khác: %s', (target) => {
  const destination = safeReturnTo('http://localhost:5173/login?returnTo=' + encodeURIComponent(target))
  expect(new URL(destination, 'http://localhost:5173').origin).toBe('http://localhost:5173')
})

const api = 'http://localhost:5173/api/v1/auth'
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('đổi mật khẩu gửi CSRF, đăng xuất và yêu cầu đăng nhập lại', async () => {
  let authenticated = true
  server.use(
    http.get(`${api}/me`, () => authenticated ? HttpResponse.json({ id: 'u1', email: 'user@example.invalid', fullName: 'Khách hàng', role: 'CUSTOMER' }) : HttpResponse.json({}, { status: 401 })),
    http.post(`${api}/password`, async ({ request }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
      if (JSON.stringify(await request.json()) !== JSON.stringify({ currentPassword: 'Old password 123', newPassword: 'New password 123@' })) return HttpResponse.json({}, { status: 400 })
      authenticated = false
      return new HttpResponse(null, { status: 204 })
    }),
  )
  const user = userEvent.setup()
  router = createMemoryRouter(appRoutes, { initialEntries: ['/account/security'] })
  render(<RouterProvider router={router} />)
  await user.type(await screen.findByLabelText('Mật khẩu hiện tại'), 'Old password 123')
  await user.type(screen.getByLabelText('Mật khẩu mới'), 'New password 123@')
  await user.click(screen.getByRole('button', { name: 'Đổi mật khẩu' }))
  await waitFor(() => expect(router?.state.location.pathname).toBe('/login'))
  expect(await screen.findByText('Đã đổi mật khẩu. Vui lòng đăng nhập lại.')).toBeVisible()
})
