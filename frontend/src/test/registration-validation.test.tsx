import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())
test('đăng ký có xác nhận, checklist và cho phép hiện mật khẩu', async () => {
  const user = userEvent.setup()
  router = createMemoryRouter(appRoutes, { initialEntries: ['/register'] })
  render(<RouterProvider router={router} />)
  const password = await screen.findByLabelText('Mật khẩu', { exact: true })
  const confirmation = screen.getByLabelText('Nhập lại mật khẩu')
  await user.type(password, 'Badminton123@')
  await user.click(screen.getByRole('button', { name: 'Hiện mật khẩu' }))
  expect(password).toHaveAttribute('type', 'text')
  await user.type(confirmation, 'Different123@')
  expect(await screen.findByText('Mật khẩu chưa khớp.')).toBeVisible()
  await user.clear(confirmation)
  await user.type(confirmation, 'Badminton123@')
  expect(await screen.findByText('Mật khẩu đã khớp.')).toBeVisible()
  expect(screen.getByText('Chữ hoa (A–Z)')).toBeVisible()
  expect(screen.getByText('Ký tự đặc biệt (!, @, #…)')).toBeVisible()
})
