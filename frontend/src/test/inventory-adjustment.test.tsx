import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import type { InventoryAdjustment, SkuInventorySummary } from '../features/inventory/inventory.api'

const api = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => router?.dispose())

function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
}

const inventorySku: SkuInventorySummary = {
  skuId: '30000000-0000-0000-0000-000000000091',
  skuCode: 'ADJ-RACKET-4U',
  optionLabel: '4U/G5',
  productId: '20000000-0000-0000-0000-000000000091',
  productTitle: 'Vợt kiểm kê',
  physicalQty: 20,
  availableQty: 15,
  reservedQty: 3,
  allocatedQty: 2,
  skuStatus: 'ACTIVE',
  publicationStatus: 'PUBLISHED',
}
const inventory: SkuInventorySummary[] = [inventorySku]

test('Staff tạo đề nghị giảm tồn nhưng giao diện không tự giảm số dư', async () => {
  let payload: unknown = null
  server.use(
    http.get(`${api}/auth/me`, () => HttpResponse.json({ id: 'staff-1', email: 'staff@example.com', fullName: 'Staff', role: 'STAFF' })),
    http.get(`${api}/auth/csrf`, () => HttpResponse.json({ requestToken: 'adjustment-csrf' })),
    http.get(`${api}/staff/products`, () => HttpResponse.json([])),
    http.get(`${api}/staff/inventory`, () => HttpResponse.json(inventory)),
    http.post(`${api}/staff/inventory/adjustment-requests`, async ({ request }) => {
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('adjustment-csrf')
      payload = await request.json()
      return HttpResponse.json({
        id: 'adjustment-1',
        productSkuId: inventorySku.skuId,
        skuCode: inventorySku.skuCode,
        productTitle: inventorySku.productTitle,
        requestedBy: 'staff-1',
        requestedByName: 'Staff',
        reviewedBy: null,
        reviewedByName: null,
        deltaQty: -4,
        reason: 'Hàng hỏng khi kiểm kê',
        reviewStatus: 'PENDING',
        createdAt: '2026-10-30T10:00:00Z',
        reviewedAt: null,
      }, { status: 201 })
    }),
  )

  const user = userEvent.setup()
  open('/staff')
  await user.click(await screen.findByRole('button', { name: /Vận hành Kho & Tồn kho/i }))
  await user.clear(screen.getByLabelText('Số lượng đề nghị giảm *'))
  await user.type(screen.getByLabelText('Số lượng đề nghị giảm *'), '4')
  await user.type(screen.getByLabelText('Lý do điều chỉnh *'), 'Hàng hỏng khi kiểm kê')
  await user.click(screen.getByRole('button', { name: /Gửi đề nghị chờ Admin duyệt/i }))

  expect(await screen.findByText(/Đã gửi đề nghị giảm 4 sản phẩm/i)).toBeVisible()
  expect(payload).toEqual({
    productSkuId: inventorySku.skuId,
    quantity: 4,
    reason: 'Hàng hỏng khi kiểm kê',
  })
  const row = screen.getByText(inventorySku.skuCode).closest('tr')
  expect(row).not.toBeNull()
  expect(row?.textContent).toContain('20')
  expect(row?.textContent).toContain('15')
})

test('Admin xem và duyệt đề nghị, UI chuyển trạng thái sang APPLIED', async () => {
  let decisionPayload: unknown = null
  let status = 'PENDING'
  const adjustment = (): InventoryAdjustment => ({
    id: 'adjustment-2',
    productSkuId: inventorySku.skuId,
    skuCode: inventorySku.skuCode,
    productTitle: inventorySku.productTitle,
    requestedBy: 'staff-1',
    requestedByName: 'Staff Kho',
    reviewedBy: status === 'PENDING' ? null : 'admin-1',
    reviewedByName: status === 'PENDING' ? null : 'Admin Duyệt',
    deltaQty: -5,
    reason: 'Sai lệch kiểm kê',
    reviewStatus: status,
    createdAt: '2026-10-30T10:00:00Z',
    reviewedAt: status === 'PENDING' ? null : '2026-10-30T10:05:00Z',
  })

  server.use(
    http.get(`${api}/auth/me`, () => HttpResponse.json({ id: 'admin-1', email: 'admin@example.com', fullName: 'Admin', role: 'ADMIN' })),
    http.get(`${api}/auth/csrf`, () => HttpResponse.json({ requestToken: 'admin-adjust-csrf' })),
    http.get(`${api}/admin/products`, () => HttpResponse.json([])),
    http.get(`${api}/admin/inventory/adjustment-requests`, () => HttpResponse.json([adjustment()])),
    http.post(`${api}/admin/inventory/adjustment-requests/adjustment-2/decision`, async ({ request }) => {
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('admin-adjust-csrf')
      decisionPayload = await request.json()
      status = 'APPLIED'
      return HttpResponse.json({
        request: adjustment(),
        balance: {
          balanceId: 'balance-1', productSkuId: inventorySku.skuId,
          physicalQty: 15, availableQty: 10, reservedQty: 3, allocatedQty: 2, revision: 2,
        },
        movementId: 'movement-1',
      })
    }),
  )

  const user = userEvent.setup()
  open('/admin')
  await user.click(await screen.findByRole('tab', { name: /Duyệt giảm tồn/i }))
  const reason = await screen.findByText('Sai lệch kiểm kê')
  const row = reason.closest('tr')!
  expect(within(row).getByText('PENDING')).toBeVisible()
  await user.click(within(row).getByRole('button', { name: 'Duyệt' }))

  expect(await screen.findByText(/Đã duyệt và áp dụng giảm 5 sản phẩm/i)).toBeVisible()
  expect(decisionPayload).toEqual({ decision: 'APPROVE' })
  expect(await screen.findByText('APPLIED')).toBeVisible()
  expect(screen.queryByRole('button', { name: 'Duyệt' })).toBeNull()
})
