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

test('Admin tạo thương hiệu với slug chuẩn và CSRF', async () => {
  let brands: unknown[] = []
  let csrf = ''
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'admin-brand', email: 'admin@example.com', fullName: 'Admin', role: 'ADMIN' })),
    http.get(`${root}/admin/products`, () => HttpResponse.json([])),
    http.get(`${root}/admin/brands`, () => HttpResponse.json(brands)),
    http.post(`${root}/admin/brands`, async ({ request }) => {
      csrf = request.headers.get('X-CSRF-TOKEN') ?? ''
      const body = await request.json() as Record<string, unknown>
      const item = { id: 'brand-yonex', ...body, productCount: 0, createdAt: '2026-11-06T00:00:00Z', updatedAt: '2026-11-06T00:00:00Z' }
      brands = [item]
      return HttpResponse.json(item, { status: 201 })
    }),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/admin?section=brands'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  expect(await screen.findByRole('heading', { name: 'Quản trị Thương hiệu' })).toBeVisible()
  await user.type(screen.getByLabelText('Tên thương hiệu'), 'Yonex Nhật Bản')
  expect(screen.getByLabelText('Slug')).toHaveValue('yonex-nhat-ban')
  await user.type(screen.getByLabelText('Quốc gia'), 'Nhật Bản')
  await user.click(screen.getByRole('button', { name: 'Tạo thương hiệu' }))
  expect(await screen.findByText('Đã tạo thương hiệu.')).toBeVisible()
  expect(await screen.findByText('Yonex Nhật Bản')).toBeVisible()
  expect(csrf).toBe('csrf-test-token')
})

test('Staff gắn thương hiệu và lưu thông số cho Product Draft', async () => {
  let receivedBrand = ''
  let specValue = ''
  let product = { id: 'product-brand', title: 'Vợt thử nghiệm', description: null, categoryId: null, brandId: null as string | null, publicationStatus: 'DRAFT', createdAt: '2026-11-06T00:00:00Z', publishedAt: null, skus: [], images: [], specifications: [] as unknown[] }
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'staff-brand', email: 'staff@example.com', fullName: 'Staff', role: 'STAFF' })),
    http.get(`${root}/staff/products`, () => HttpResponse.json([product])),
    http.get(`${root}/staff/brands`, () => HttpResponse.json([{ id: 'brand-victor', name: 'Victor', slug: 'victor', country: 'Taiwan', websiteUrl: null, isActive: true, productCount: 0, createdAt: '2026-11-06T00:00:00Z', updatedAt: '2026-11-06T00:00:00Z' }])),
    http.post(`${root}/staff/products`, async ({ request }) => { const body = await request.json() as { brandId?: string }; receivedBrand = body.brandId ?? ''; product = { ...product, id: 'new-product', title: 'Vợt Victor mới', brandId: receivedBrand }; return HttpResponse.json(product, { status: 201 }) }),
    http.put(`${root}/staff/products/:id/specifications/:key`, async ({ params, request }) => { const body = await request.json() as { displayName: string; value: string; displayOrder: number }; specValue = body.value; return HttpResponse.json({ id: 'spec-1', key: params.key, ...body }) }),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  expect(await screen.findByRole('heading', { name: 'Vợt thử nghiệm', level: 2 })).toBeVisible()
  await user.type(screen.getByLabelText('Tên sản phẩm *'), 'Vợt Victor mới')
  await user.selectOptions(screen.getByLabelText('Thương hiệu', { selector: '#draft-brand' }), 'brand-victor')
  await user.click(screen.getByRole('button', { name: 'Tạo bản nháp' }))
  expect(receivedBrand).toBe('brand-victor')
  await user.click(await screen.findByRole('button', { name: /Vợt Victor mới/ }))
  await user.type(screen.getByLabelText('Khóa kỹ thuật'), 'trong_luong')
  await user.type(screen.getByLabelText('Tên hiển thị'), 'Trọng lượng')
  await user.type(screen.getByLabelText('Giá trị'), '4U')
  await user.click(screen.getByRole('button', { name: 'Lưu thông số' }))
  expect(await screen.findByText('Đã lưu thông số kỹ thuật.')).toBeVisible()
  expect(specValue).toBe('4U')
})
