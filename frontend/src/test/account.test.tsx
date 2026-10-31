import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const authApi = 'http://localhost:5173/api/v1/auth'
const accountApi = 'http://localhost:5173/api/v1/account'

const customer = { id: 'customer-1', email: 'badminton.fan@example.com', fullName: 'Nguyễn Cầu Lông', role: 'CUSTOMER' }
const profileData = { id: 'customer-1', email: 'badminton.fan@example.com', fullName: 'Nguyễn Cầu Lông', phone: '0901234567', role: 'CUSTOMER' }
const initialAddresses = [
  {
    id: 'addr-1',
    recipientName: 'Nguyễn Cầu Lông',
    phone: '0901234567',
    addressLine: '123 Đường Cầu Lông',
    ward: 'Phường 1',
    province: 'TP. Hồ Chí Minh',
    isDefault: true,
    createdAt: '2026-10-30T10:00:00Z',
  },
]

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

test('khách chưa đăng nhập vào /account bị chuyển hướng sang login', async () => {
  server.use(http.get(`${authApi}/me`, () => HttpResponse.json({}, { status: 401 })))
  const route = open('/account')
  await waitFor(() => expect(route.state.location.pathname).toBe('/login'))
  expect(route.state.location.search).toBe('?returnTo=%2Faccount')
})

test('customer xem được thông tin hồ sơ và danh sách địa chỉ với layout responsive', async () => {
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(customer)),
    http.get(`${accountApi}/profile`, () => HttpResponse.json(profileData)),
    http.get(`${accountApi}/addresses`, () => HttpResponse.json(initialAddresses)),
  )

  const { container } = render(<RouterProvider router={createMemoryRouter(appRoutes, { initialEntries: ['/account'] })} />)

  expect(await screen.findByDisplayValue(profileData.fullName)).toBeVisible()
  expect(screen.getByDisplayValue(profileData.phone)).toBeVisible()
  expect(screen.getByDisplayValue(profileData.email)).toBeDisabled()

  expect(screen.getByText('123 Đường Cầu Lông, Phường 1, TP. Hồ Chí Minh')).toBeVisible()
  expect(screen.getByText('Mặc định')).toBeVisible()

  // Kiểm tra class responsive không dùng inline gridTemplateColumns
  const layout = container.querySelector('.account-layout')
  expect(layout).not.toBeNull()
  expect((layout as HTMLElement).style.gridTemplateColumns).toBe('')
})

test('cập nhật hồ sơ gửi CSRF và hiển thị thông báo thành công', async () => {
  let patchedData: unknown = null
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(customer)),
    http.get(`${accountApi}/profile`, () => HttpResponse.json(profileData)),
    http.get(`${accountApi}/addresses`, () => HttpResponse.json(initialAddresses)),
    http.patch(`${accountApi}/profile`, async ({ request }) => {
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('csrf-test-token')
      patchedData = await request.json()
      return HttpResponse.json({ ...profileData, ...(patchedData as object) })
    }),
  )

  const user = userEvent.setup()
  open('/account')

  const nameInput = await screen.findByDisplayValue(profileData.fullName)
  await user.clear(nameInput)
  await user.type(nameInput, 'Nguyễn Vợt Cầu Mới')

  await user.click(screen.getByRole('button', { name: 'Lưu hồ sơ' }))

  expect(await screen.findByText(/Cập nhật hồ sơ thành công/)).toBeVisible()
  expect(patchedData).toEqual({ fullName: 'Nguyễn Vợt Cầu Mới', phone: '0901234567' })
})

test('sửa nội dung địa chỉ mở form chỉnh sửa, gửi PATCH và cập nhật', async () => {
  let patchedPayload: unknown = null
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(customer)),
    http.get(`${accountApi}/profile`, () => HttpResponse.json(profileData)),
    http.get(`${accountApi}/addresses`, () => HttpResponse.json(initialAddresses)),
    http.patch(`${accountApi}/addresses/addr-1`, async ({ request }) => {
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('csrf-test-token')
      patchedPayload = await request.json()
      return HttpResponse.json({
        id: 'addr-1',
        recipientName: 'Nguyễn Cầu Lông Đã Sửa',
        phone: '0909998877',
        addressLine: '456 Đường Sân Cầu',
        ward: 'Phường 2',
        province: 'TP. Hồ Chí Minh',
        isDefault: true,
        createdAt: '2026-10-30T10:00:00Z',
      })
    }),
  )

  const user = userEvent.setup()
  open('/account')

  // Bấm nút Sửa
  const editButton = await screen.findByRole('button', { name: 'Sửa' })
  await user.click(editButton)

  // Kiểm tra form sửa xuất hiện với prefilled values
  const saveButton = screen.getByRole('button', { name: 'Lưu thay đổi' })
  const editForm = saveButton.closest('form')!
  const formScope = within(editForm)

  const nameInput = formScope.getByLabelText('Tên người nhận *')
  expect(nameInput).toHaveValue('Nguyễn Cầu Lông')

  await user.clear(nameInput)
  await user.type(nameInput, 'Nguyễn Cầu Lông Đã Sửa')

  const phoneInput = formScope.getByLabelText('Số điện thoại *')
  await user.clear(phoneInput)
  await user.type(phoneInput, '0909998877')

  const lineInput = formScope.getByLabelText('Địa chỉ chi tiết *')
  await user.clear(lineInput)
  await user.type(lineInput, '456 Đường Sân Cầu')

  // Bấm Lưu thay đổi
  await user.click(saveButton)

  expect(await screen.findByText(/Cập nhật địa chỉ thành công/)).toBeVisible()
  expect(patchedPayload).toEqual({
    recipientName: 'Nguyễn Cầu Lông Đã Sửa',
    phone: '0909998877',
    addressLine: '456 Đường Sân Cầu',
    ward: 'Phường 1',
    province: 'TP. Hồ Chí Minh',
    isDefault: true,
  })
})

test('thêm địa chỉ mới gửi đầy đủ trường và CSRF', async () => {
  let createdPayload: unknown = null
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(customer)),
    http.get(`${accountApi}/profile`, () => HttpResponse.json(profileData)),
    http.get(`${accountApi}/addresses`, () => HttpResponse.json([])),
    http.post(`${accountApi}/addresses`, async ({ request }) => {
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('csrf-test-token')
      createdPayload = await request.json()
      return HttpResponse.json({
        id: 'new-addr-id',
        recipientName: 'Người Nhận 2',
        phone: '0988776655',
        addressLine: '999 Đường Cầu Lông',
        ward: null,
        province: 'Hà Nội',
        isDefault: true,
        createdAt: '2026-10-30T10:05:00Z',
      }, { status: 201 })
    }),
  )

  const user = userEvent.setup()
  open('/account')

  await user.type(await screen.findByLabelText('Tên người nhận *'), 'Người Nhận 2')
  await user.type(screen.getByLabelText('Số điện thoại *'), '0988776655')
  await user.type(screen.getByLabelText(/Địa chỉ chi tiết/i), '999 Đường Cầu Lông')
  await user.type(screen.getByLabelText('Tỉnh / Thành phố *'), 'Hà Nội')

  await user.click(screen.getByRole('button', { name: 'Thêm địa chỉ' }))

  expect(await screen.findByText(/Thêm địa chỉ mới thành công/)).toBeVisible()
  expect(createdPayload).toEqual({
    recipientName: 'Người Nhận 2',
    phone: '0988776655',
    addressLine: '999 Đường Cầu Lông',
    ward: null,
    province: 'Hà Nội',
    isDefault: true,
  })
})

test('xóa địa chỉ đang gắn với đơn hàng hiển thị lỗi 409 conflict', async () => {
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(customer)),
    http.get(`${accountApi}/profile`, () => HttpResponse.json(profileData)),
    http.get(`${accountApi}/addresses`, () => HttpResponse.json(initialAddresses)),
    http.delete(`${accountApi}/addresses/addr-1`, () => {
      return HttpResponse.json({ code: 'ADDRESS_IN_USE', title: 'Địa chỉ đã gắn với đơn hàng, không thể xóa.' }, { status: 409 })
    }),
  )

  // Mock window.confirm to return true
  window.confirm = () => true

  const user = userEvent.setup()
  open('/account')

  const deleteButton = await screen.findByRole('button', { name: 'Xóa' })
  await user.click(deleteButton)

  expect(await screen.findByRole('alert')).toHaveTextContent('Địa chỉ này đã được sử dụng trong đơn hàng, không thể xóa.')
})
