import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Staff mở bảng tổng hợp tác vụ vận hành', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'staff-report', email: 'staff@example.invalid', fullName: 'Staff Report', role: 'STAFF' })),
    http.get(`${root}/staff/products`, () => HttpResponse.json([])),
    http.get(`${root}/staff/dashboard/tasks`, () => HttpResponse.json({
      pendingCodCount: 3, shipmentsToPackCount: 2, shipmentsToHandoverCount: 1,
      deliveryFailuresCount: 4, lowStockSkuCount: 5, notificationsUnreadCount: 6,
    })),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  await userEvent.setup().click(await screen.findByRole('button', { name: /Tác vụ cần xử lý/ }))
  expect(await screen.findByRole('heading', { name: 'Tác vụ cần xử lý' })).toBeVisible()
  expect(screen.getByText('COD chờ xác nhận').nextElementSibling).toHaveTextContent('3')
  expect(screen.getByText('SKU tồn thấp').nextElementSibling).toHaveTextContent('5')
})

test('Admin xem doanh thu xác minh, tồn thấp và Audit Log', async () => {
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'admin-report', email: 'admin@example.invalid', fullName: 'Admin Report', role: 'ADMIN' })),
    http.get(`${root}/admin/products`, () => HttpResponse.json([])),
    http.get(`${root}/admin/reports/revenue`, () => HttpResponse.json({
      from: '2026-10-01', to: '2026-10-30', revenue: 1500000, completedOrderCount: 2, averageOrderValue: 750000,
      days: [{ date: '2026-10-30', revenue: 1500000, completedOrderCount: 2 }],
      paymentMethods: [{ paymentMethod: 'COD', revenue: 1500000, completedOrderCount: 2 }],
    })),
    http.get(`${root}/admin/reports/low-stock`, () => HttpResponse.json({ threshold: 5, items: [{
      productSkuId: 'sku-low', productTitle: 'Vợt High Pro', skuCode: 'HIGH-4U', optionLabel: '4U G5',
      availableQty: 2, physicalQty: 4, reservedQty: 1, allocatedQty: 1,
    }] })),
    http.get(`${root}/admin/audit-logs`, () => HttpResponse.json({ page: 1, pageSize: 30, totalCount: 1, items: [{
      id: 'audit-1', actorId: 'admin-report', actorType: 'USER', actorName: 'Admin Report', action: 'PRODUCT_PUBLISHED',
      entityType: 'Product', entityId: 'product-1', beforeData: '{}', afterData: '{}', correlationId: 'corr-1', occurredAt: '2026-10-30T08:00:00Z',
    }] })),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
  render(<RouterProvider router={router} />)
  await userEvent.setup().click(await screen.findByRole('tab', { name: /Báo cáo & Audit/ }))
  expect(await screen.findByText(/1\.500\.000/)).toBeVisible()
  expect(screen.getByText('HIGH-4U')).toBeVisible()
  expect(screen.getByText('PRODUCT_PUBLISHED')).toBeVisible()
})
