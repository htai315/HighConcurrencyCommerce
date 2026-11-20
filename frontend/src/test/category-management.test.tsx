import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import type { AdminCategory } from '../features/categories/category-management.api'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Admin quản trị danh mục: tạo slug, ẩn Storefront và bảo vệ danh mục đang dùng', async () => {
  let categories: AdminCategory[] = [{
    id: 'category-used', name: 'Vợt cầu lông', slug: 'vot-cau-long', isVisible: true, productCount: 3,
    createdAt: '2026-11-06T00:00:00Z', updatedAt: '2026-11-06T00:00:00Z',
  }]
  let receivedCsrf = ''
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'admin-category', email: 'admin@example.com', fullName: 'Admin', role: 'ADMIN' })),
    http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'category-csrf' })),
    http.get(`${root}/admin/products`, () => HttpResponse.json([])),
    http.get(`${root}/admin/categories`, () => HttpResponse.json(categories)),
    http.post(`${root}/admin/categories`, async ({ request }) => {
      receivedCsrf = request.headers.get('X-CSRF-TOKEN') ?? ''
      const body = await request.json() as { name: string; slug: string; isVisible: boolean }
      const created: AdminCategory = { id: 'category-new', ...body, productCount: 0, createdAt: '2026-11-06T01:00:00Z', updatedAt: '2026-11-06T01:00:00Z' }
      categories = [...categories, created]
      return HttpResponse.json(created, { status: 201 })
    }),
    http.patch(`${root}/admin/categories/:id`, async ({ params, request }) => {
      const body = await request.json() as { name: string; slug: string; isVisible: boolean }
      categories = categories.map(item => item.id === params.id ? { ...item, ...body } : item)
      return HttpResponse.json(categories.find(item => item.id === params.id))
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/admin?section=categories'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  expect(await screen.findByRole('heading', { name: 'Quản trị Danh mục Sản phẩm' })).toBeVisible()
  expect(await screen.findByText('Vợt cầu lông')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Xóa' })).toBeDisabled()

  await user.type(screen.getByLabelText('Tên danh mục'), 'Túi và balo cầu lông')
  expect(screen.getByLabelText('Slug URL')).toHaveValue('tui-va-balo-cau-long')
  await user.click(screen.getByRole('button', { name: 'Tạo danh mục' }))
  expect(await screen.findByText('Đã tạo danh mục mới.')).toBeVisible()
  expect(receivedCsrf).toBe('category-csrf')
  expect(await screen.findByText('Túi và balo cầu lông')).toBeVisible()

  const newRow = screen.getByText('Túi và balo cầu lông').closest('article')!
  await user.click(withinRow(newRow, 'Ẩn'))
  await waitFor(() => expect(withinRow(newRow, 'Hiện')).toBeVisible())
})

test('Staff gắn danh mục đã được Admin quản trị khi tạo Product Draft', async () => {
  let categoryId = ''
  let products: unknown[] = []
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json({ id: 'staff-category', email: 'staff@example.com', fullName: 'Staff', role: 'STAFF' })),
    http.get(`${root}/staff/categories`, () => HttpResponse.json([{
      id: 'category-bag', name: 'Túi cầu lông', slug: 'tui-cau-long', isVisible: true, productCount: 0,
      createdAt: '2026-11-06T00:00:00Z', updatedAt: '2026-11-06T00:00:00Z',
    }])),
    http.get(`${root}/staff/products`, () => HttpResponse.json(products)),
    http.post(`${root}/staff/products`, async ({ request }) => {
      const body = await request.json() as { title: string; description?: string; categoryId?: string }
      categoryId = body.categoryId ?? ''
      const product = { id: 'draft-category', title: body.title, description: body.description ?? null, categoryId, publicationStatus: 'DRAFT', createdAt: '2026-11-06T00:00:00Z', publishedAt: null, skus: [], images: [] }
      products = [product]
      return HttpResponse.json(product, { status: 201 })
    }),
  )
  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  expect(await screen.findByRole('heading', { name: /Quản trị Sản phẩm & Media/i })).toBeVisible()
  await user.type(screen.getByLabelText('Tên sản phẩm *'), 'Túi thi đấu Pro')
  await user.selectOptions(screen.getByLabelText('Danh mục'), 'category-bag')
  await user.click(screen.getByRole('button', { name: 'Tạo bản nháp' }))
  expect(await screen.findByText(/Tạo sản phẩm nháp "Túi thi đấu Pro" thành công/)).toBeVisible()
  expect(categoryId).toBe('category-bag')
})

function withinRow(row: HTMLElement, buttonName: string): HTMLElement {
  const button = Array.from(row.querySelectorAll('button')).find(item => item.textContent === buttonName)
  if (!button) throw new Error(`Missing ${buttonName} button`)
  return button
}
