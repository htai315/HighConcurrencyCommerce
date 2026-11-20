import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { AdminShippingPanel } from '../features/shipping/AdminShippingPanel'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const method = {
  id: 'shipping-standard', code: 'STANDARD', name: 'Giao hàng tiêu chuẩn', description: 'Giao trong giờ hành chính',
  methodStatus: 'ACTIVE', baseFee: 30000, freeShippingThreshold: 500000, estimatedMinDays: 2, estimatedMaxDays: 4,
  isDefault: true, displayOrder: 10, revision: 3, provinceRateCount: 0,
  createdAt: '2026-11-06T00:00:00Z', updatedAt: '2026-11-06T00:00:00Z', provinceRates: [],
}

function summary(value = method) {
  const { description: _description, createdAt: _createdAt, provinceRates, ...rest } = value
  return { ...rest, provinceRateCount: provinceRates.length }
}

function baseHandlers(detail = method) {
  server.use(
    http.get(`${root}/admin/shipping-methods`, () => HttpResponse.json([summary(detail)])),
    http.get(`${root}/admin/shipping-methods/:id`, () => HttpResponse.json(detail)),
  )
}

test('Admin sửa cấu hình với expectedRevision và giao diện tải revision mới', async () => {
  baseHandlers()
  let payload: Record<string, unknown> | null = null
  const updated = { ...method, baseFee: 35000, revision: 4, updatedAt: '2026-11-06T01:00:00Z' }
  server.use(http.patch(`${root}/admin/shipping-methods/:id`, async ({ request }) => {
    payload = await request.json() as Record<string, unknown>
    return HttpResponse.json(updated)
  }))
  const user = userEvent.setup()
  render(<AdminShippingPanel />)
  expect(await screen.findByRole('heading', { name: 'Giao hàng tiêu chuẩn' })).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Chỉnh sửa' }))
  const fee = screen.getByLabelText('Phí cơ bản (VNĐ)')
  await user.clear(fee); await user.type(fee, '35000')
  await user.click(screen.getByRole('button', { name: 'Lưu cấu hình' }))
  expect(await screen.findByRole('status')).toHaveTextContent('Đã cập nhật')
  expect(payload).toMatchObject({ baseFee: 35000, expectedRevision: 3, name: 'Giao hàng tiêu chuẩn' })
  expect(screen.getByText('35.000 ₫')).toBeVisible()
})

test('revision stale không ghi đè và tự tải cấu hình mới nhất', async () => {
  let detailReads = 0
  const latest = { ...method, baseFee: 42000, revision: 4, updatedAt: '2026-11-06T02:00:00Z' }
  server.use(
    http.get(`${root}/admin/shipping-methods`, () => HttpResponse.json([summary(detailReads > 1 ? latest : method)])),
    http.get(`${root}/admin/shipping-methods/:id`, () => { detailReads += 1; return HttpResponse.json(detailReads === 1 ? method : latest) }),
    http.patch(`${root}/admin/shipping-methods/:id`, () => HttpResponse.json({ code: 'SHIPPING_METHOD_STALE', message: 'Stale revision' }, { status: 409 })),
  )
  const user = userEvent.setup()
  render(<AdminShippingPanel />)
  await screen.findByRole('heading', { name: 'Giao hàng tiêu chuẩn' })
  await user.click(screen.getByRole('button', { name: 'Chỉnh sửa' }))
  await user.click(screen.getByRole('button', { name: 'Lưu cấu hình' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Dữ liệu mới nhất đã được tải lại')
  expect((await screen.findAllByText(/42\.000/)).length).toBeGreaterThan(0)
})

test('Admin tạo phương thức mới qua CSRF contract', async () => {
  baseHandlers()
  const requests: Array<{ path: string; body: unknown }> = []
  const express = { ...method, id: 'shipping-express', code: 'EXPRESS', name: 'Giao hỏa tốc', description: null, methodStatus: 'INACTIVE', isDefault: false, revision: 1, baseFee: 60000 }
  server.use(
    http.post(`${root}/admin/shipping-methods`, async ({ request }) => {
      requests.push({ path: 'create', body: await request.json() }); return HttpResponse.json(express, { status: 201 })
    }),
  )
  const user = userEvent.setup()
  render(<AdminShippingPanel />)
  await screen.findByRole('heading', { name: 'Giao hàng tiêu chuẩn' })
  await user.click(screen.getByRole('button', { name: 'Tạo phương thức' }))
  await user.type(screen.getByLabelText('Mã phương thức'), 'EXPRESS')
  await user.type(screen.getByLabelText('Tên hiển thị'), 'Giao hỏa tốc')
  await user.click(screen.getAllByRole('button', { name: 'Tạo phương thức' })[1]!)
  await waitFor(() => expect(requests.some(request => request.path === 'create')).toBe(true))
  expect(requests.find(request => request.path === 'create')?.body).toMatchObject({ code: 'EXPRESS', name: 'Giao hỏa tốc', baseFee: 30000 })
})

test('Admin lưu phụ phí tỉnh với revision hiện tại', async () => {
  baseHandlers()
  let payload: unknown
  server.use(http.put(`${root}/admin/shipping-methods/:id/province-rates/:province`, async ({ request }) => {
    payload = await request.json()
    return HttpResponse.json({ ...method, revision: 4, provinceRates: [{ id: 'rate-hn', provinceKey: 'HA NOI', surchargeFee: 15000, isActive: true, updatedAt: '2026-11-06T03:00:00Z' }] })
  }))
  const user = userEvent.setup()
  render(<AdminShippingPanel />)
  await screen.findByRole('heading', { name: 'Giao hàng tiêu chuẩn' })

  await user.type(screen.getByLabelText('Tỉnh/thành'), 'Hà Nội')
  const surcharge = screen.getByLabelText('Phụ phí (VNĐ)'); await user.clear(surcharge); await user.type(surcharge, '15000')
  await user.click(screen.getByRole('button', { name: 'Lưu phụ phí' }))
  expect(await screen.findByText('HA NOI')).toBeVisible()
  expect(payload).toEqual({ surchargeFee: 15000, isActive: true, expectedRevision: 3 })
})

test('Admin kích hoạt phương thức qua ConfirmDialog', async () => {
  const inactive = { ...method, methodStatus: 'INACTIVE', isDefault: false }
  baseHandlers(inactive)
  let transitionBody: unknown
  server.use(http.post(`${root}/admin/shipping-methods/:id/activate`, async ({ request }) => {
    transitionBody = await request.json()
    return HttpResponse.json({ ...inactive, methodStatus: 'ACTIVE', revision: 4 })
  }))
  const user = userEvent.setup()
  render(<AdminShippingPanel />)
  await screen.findByRole('heading', { name: 'Giao hàng tiêu chuẩn' })
  await user.click(screen.getByRole('button', { name: 'Kích hoạt' }))
  await user.click(screen.getByRole('button', { name: 'Xác nhận' }))
  await waitFor(() => expect(transitionBody).toEqual({ expectedRevision: 3 }))
})
