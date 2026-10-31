import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const api = 'http://localhost:5173/api/v1/auth'
const customer = { id: 'customer-1', email: 'customer@example.invalid', fullName: 'Khách thử nghiệm', role: 'CUSTOMER' }
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())
function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

test('khách vào orders được chuyển login rồi trở lại đúng trang sau đăng nhập', async () => {
  let authenticated = false
  server.use(
    http.get(`${api}/me`, () => authenticated ? HttpResponse.json(customer) : HttpResponse.json({}, { status: 401 })),
    http.post(`${api}/login`, async ({ request }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
      const body = await request.json()
      if (JSON.stringify(body) !== JSON.stringify({ email: customer.email, password: 'Valid password 123' })) return HttpResponse.json({}, { status: 401 })
      authenticated = true
      return HttpResponse.json(customer)
    }),
  )
  const user = userEvent.setup()
  const route = open('/orders')
  await user.type(await screen.findByLabelText('Email'), customer.email)
  await user.type(screen.getByLabelText('Mật khẩu'), 'Valid password 123')
  await user.click(screen.getByRole('button', { name: 'Đăng nhập' }))
  await waitFor(() => expect(route.state.location.pathname).toBe('/orders'))
  expect(await screen.findByText(customer.fullName)).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Tài khoản của bạn' }))
  expect(screen.getByRole('button', { name: 'Đăng xuất' })).toBeVisible()
  expect(localStorage.length).toBe(0)
})

test('login sai giữ email và hiển thị lỗi chung', async () => {
  server.use(http.post(`${api}/login`, () => HttpResponse.json({ code: 'INVALID_CREDENTIALS', title: 'private account state' }, { status: 401 })))
  const user = userEvent.setup()
  open('/login')
  await user.type(await screen.findByLabelText('Email'), customer.email)
  await user.type(screen.getByLabelText('Mật khẩu'), 'wrong')
  await user.click(screen.getByRole('button', { name: 'Đăng nhập' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Email hoặc mật khẩu không đúng')
  expect(screen.getByLabelText('Email')).toHaveValue(customer.email)
  expect(screen.queryByText('private account state')).toBeNull()
})

test('đăng ký dùng CSRF và hướng người dùng đăng nhập, không tự cấp session', async () => {
  server.use(http.post(`${api}/register`, async ({ request }) => {
    const body = await request.json()
    if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token' || !body || typeof body !== 'object' || 'role' in body) return HttpResponse.json({}, { status: 400 })
    return HttpResponse.json({ message: 'generic' }, { status: 202 })
  }))
  const user = userEvent.setup()
  const route = open('/register')
  await user.type(await screen.findByLabelText('Họ và tên'), customer.fullName)
  await user.type(screen.getByLabelText('Email'), customer.email)
  await user.type(screen.getByLabelText('Mật khẩu', { exact: true }), 'Valid password 123@')
  await user.type(screen.getByLabelText('Nhập lại mật khẩu'), 'Valid password 123@')
  await user.click(screen.getByRole('checkbox', { name: /Tôi đồng ý/ }))
  await user.click(screen.getByRole('button', { name: 'Tạo tài khoản' }))
  await waitFor(() => expect(route.state.location.pathname).toBe('/login'))
  expect(await screen.findByRole('status')).toHaveTextContent('Bạn có thể đăng nhập')
})

test('Customer không xem trang Admin dù nhập URL trực tiếp', async () => {
  server.use(http.get(`${api}/me`, () => HttpResponse.json(customer)))
  open('/admin')
  expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền truy cập')
  expect(screen.queryByRole('link', { name: 'Quản trị' })).toBeNull()
})

test('đăng xuất xóa trạng thái user và guard kiểm tra lại khi quay lại trang riêng', async () => {
  let authenticated = true
  server.use(
    http.get(`${api}/me`, () => authenticated ? HttpResponse.json(customer) : HttpResponse.json({}, { status: 401 })),
    http.post(`${api}/logout`, ({ request }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 400 })
      authenticated = false
      return new HttpResponse(null, { status: 204 })
    }),
    http.get('http://localhost:5173/api/v1/catalog/categories', () => HttpResponse.json([])),
    http.get('http://localhost:5173/api/v1/catalog/products', () => HttpResponse.json({ items: [], totalCount: 0, page: 1, pageSize: 12 })),
  )
  const user = userEvent.setup()
  const route = open('/orders')
  await user.click(await screen.findByRole('button', { name: 'Tài khoản của bạn' }))
  await user.click(await screen.findByRole('button', { name: 'Đăng xuất' }))
  expect(await screen.findByRole('link', { name: 'Đăng nhập' })).toBeVisible()
  await user.click(screen.getByRole('link', { name: 'Đơn hàng' }))
  await waitFor(() => expect(route.state.location.pathname).toBe('/login'))
})
