import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import type { ManagedProduct } from '../features/product-management/product-management.api'

const authApi = 'http://localhost:5173/api/v1/auth'
const staffApi = 'http://localhost:5173/api/v1/staff'
const adminApi = 'http://localhost:5173/api/v1/admin'

const staffUser = { id: 'staff-1', email: 'staff@example.com', fullName: 'Staff Vận Hành', role: 'STAFF' }
const adminUser = { id: 'admin-1', email: 'admin@example.com', fullName: 'Admin Quản Trị', role: 'ADMIN' }

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

test('Staff: tạo sản phẩm nháp mới, thêm SKU và thêm ảnh media', async () => {
  const createdProduct: ManagedProduct = {
    id: 'prod-new-1',
    title: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
    description: 'Vợt trợ lực cao cấp',
    categoryId: null,
    brandId: null,
    publicationStatus: 'DRAFT',
    createdAt: '2026-10-30T10:00:00Z',
    publishedAt: null,
    skus: [],
    images: [],
    specifications: [],
  }

  let productState: ManagedProduct = { ...createdProduct }

  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(staffUser)),
    http.get(`${authApi}/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
    http.get(`${staffApi}/products`, () => HttpResponse.json([productState])),
    http.post(`${staffApi}/products`, async ({ request }) => {
      const body = (await request.json()) as { title: string; description?: string }
      productState = {
        ...productState,
        title: body.title,
        description: body.description ?? null,
      }
      return HttpResponse.json(productState, { status: 201 })
    }),
    http.post(`${staffApi}/products/:id/skus`, async ({ request }) => {
      const body = (await request.json()) as { skuCode: string; optionLabel: string }
      const newSku = {
        id: 'sku-1',
        productId: productState.id,
        skuCode: body.skuCode,
        optionLabel: body.optionLabel,
        regularPrice: 0,
        skuStatus: 'ACTIVE',
      }
      productState = {
        ...productState,
        skus: [...productState.skus, newSku as never],
      }
      return HttpResponse.json(newSku, { status: 201 })
    }),
    http.post(`${staffApi}/products/:id/images/upload`, async ({ request }) => {
      const body = await request.formData()
      const file = body.get('file') as File
      const newImg = {
        id: 'img-1',
        productId: productState.id,
        fileKey: '/api/v1/media/products/prod-new-1/uploaded.webp',
        altText: body.get('altText')?.toString() ?? null,
        displayOrder: Number(body.get('displayOrder') ?? 0),
        imageStatus: 'ACTIVE',
      }
      expect(file.type).toBe('image/webp')
      expect(file.size).toBeGreaterThan(0)
      productState = {
        ...productState,
        images: [...productState.images, newImg as never],
      }
      return HttpResponse.json(newImg, { status: 201 })
    }),
    http.delete(`${staffApi}/products/:id/images/:imgId`, () => {
      productState = {
        ...productState,
        images: [],
      }
      return new HttpResponse(null, { status: 204 })
    }),
  )

  const user = userEvent.setup()
  open('/staff')

  // 1. Kiểm tra header và trang staff đã load
  expect(await screen.findByRole('heading', { name: /Quản trị Sản phẩm & Media/i })).toBeVisible()
  expect(screen.getByRole('heading', { name: 'Vợt Cầu Lông Yonex Nanoflare 1000Z', level: 2 })).toBeVisible()

  // 2. Thêm SKU mới
  await user.type(screen.getByLabelText(/Mã SKU/i), 'NF1000Z-4UG5')
  await user.type(screen.getByLabelText(/Tên phiên bản \/ Tùy chọn/i), '4U - Cán G5 Vàng Sấm Sét')
  await user.click(screen.getByRole('button', { name: /\+ Thêm SKU/i }))

  expect(await screen.findByText(/Thêm biến thể SKU thành công/i)).toBeVisible()
  expect(await screen.findByText('NF1000Z-4UG5')).toBeVisible()

  // 3. Thêm ảnh mới
  const imageFile = new File([new Uint8Array([82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80])], 'nf1000z-main.webp', { type: 'image/webp' })
  await user.upload(screen.getByLabelText(/Kéo ảnh vào đây/i), imageFile)
  await user.type(screen.getByLabelText(/Alt text chung/i), 'Mặt trước vợt Nanoflare')
  await user.click(screen.getByRole('button', { name: /Upload 1 ảnh/i }))

  expect(await screen.findByText(/Đã upload 1 ảnh thành công/i)).toBeVisible()
  expect(await screen.findByText('/api/v1/media/products/prod-new-1/uploaded.webp')).toBeVisible()

  // 4. Xóa ảnh
  const deleteBtn = await screen.findByRole('button', { name: /Xóa ảnh/i })
  await user.click(deleteBtn)
  await user.click(screen.getByRole('button', { name: 'Xác nhận xóa' }))
  expect(await screen.findByText(/Xóa ảnh thành công/i)).toBeVisible()
})

test('Admin: xem sản phẩm, định giá biến thể SKU và Publish sản phẩm', async () => {
  let productState: ManagedProduct = {
    id: 'prod-admin-1',
    title: 'Vợt Victor Thruster Ryuga II',
    description: 'Vợt tấn công chuyên nghiệp',
    categoryId: null,
      brandId: null,
    publicationStatus: 'DRAFT',
    createdAt: '2026-10-30T10:00:00Z',
    publishedAt: null,
    skus: [
      {
        id: 'sku-admin-1',
        productId: 'prod-admin-1',
        skuCode: 'TK-RYUGA-II',
        optionLabel: '3U - G5 Rồng Lửa',
        regularPrice: 0,
        skuStatus: 'ACTIVE',
      },
    ],
    images: [],
      specifications: [],
  }

  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(adminUser)),
    http.get(`${authApi}/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
    http.get(`${adminApi}/products`, () => HttpResponse.json([productState])),
    http.patch(`${adminApi}/skus/:id/price`, async ({ request }) => {
      const body = (await request.json()) as { price: number }
      productState = {
        ...productState,
        skus: productState.skus.map(s => (s.id === 'sku-admin-1' ? { ...s, regularPrice: body.price } : s)),
      }
      return HttpResponse.json(productState.skus[0])
    }),
    http.post(`${adminApi}/products/:id/publish`, () => {
      productState = {
        ...productState,
        publicationStatus: 'PUBLISHED',
        publishedAt: '2026-10-30T12:00:00Z' as never,
      }
      return HttpResponse.json({
        id: productState.id,
        publicationStatus: 'PUBLISHED',
        publishedAt: '2026-10-30T12:00:00Z',
      })
    }),
  )

  const user = userEvent.setup()
  open('/admin')

  // 1. Kiểm tra header và danh sách
  expect(await screen.findByRole('heading', { name: /Kiểm soát Giá & Phê duyệt Công bố/i })).toBeVisible()
  expect(screen.getByRole('heading', { name: 'Vợt Victor Thruster Ryuga II', level: 2 })).toBeVisible()

  // Nút publish lúc đầu bị disable do SKU có regularPrice = 0
  const publishBtn = screen.getByRole('button', { name: 'Công bố sản phẩm' })
  expect(publishBtn).toBeDisabled()

  // 2. Nhập giá mới cho SKU và lưu
  const priceInput = screen.getByPlaceholderText('VD: 3200000')
  await user.type(priceInput, '3750000')

  const savePriceBtn = screen.getByRole('button', { name: /Lưu giá/i })
  expect(savePriceBtn).toBeEnabled()
  await user.click(savePriceBtn)

  expect(await screen.findByText(/Cập nhật giá thành công/i)).toBeVisible()

  // 3. Sau khi cập nhật giá, nút Publish được enable
  await waitFor(() => expect(publishBtn).toBeEnabled())

  // 4. Click Publish
  await user.click(publishBtn)
  await user.click(screen.getByRole('button', { name: 'Xác nhận' }))

  expect(await screen.findByText(/Đã công bố sản phẩm thành công lên Catalog/i)).toBeVisible()
  expect(await screen.findByText(/Sản phẩm này đang hoạt động trên Catalog công khai/i)).toBeVisible()
})

test('Admin: tạm ngừng và mở bán lại sản phẩm với lý do vận hành', async () => {
  let productState: ManagedProduct = {
    id: 'prod-lifecycle-1', title: 'Vợt Yonex Lifecycle', description: 'Đang bán', categoryId: null, brandId: null,
    publicationStatus: 'PUBLISHED', createdAt: '2026-11-06T10:00:00Z', publishedAt: '2026-11-06T11:00:00Z',
    statusChangedAt: '2026-11-06T11:00:00Z', statusChangedBy: 'admin-1', statusReason: null,
    skus: [{ id: 'sku-life-1', productId: 'prod-lifecycle-1', skuCode: 'LIFE-1', optionLabel: '4U/G5', regularPrice: 1_500_000, skuStatus: 'ACTIVE' }], images: [], specifications: [],
  }
  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(adminUser)),
    http.get(`${authApi}/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
    http.get(`${adminApi}/products`, () => HttpResponse.json([productState])),
    http.post(`${adminApi}/products/:id/suspend`, async ({ request }) => {
      const body = (await request.json()) as { reason: string }
      productState = { ...productState, publicationStatus: 'SUSPENDED', statusReason: body.reason, statusChangedAt: '2026-11-06T12:00:00Z' }
      return HttpResponse.json({ productId: productState.id, publicationStatus: productState.publicationStatus, statusChangedAt: productState.statusChangedAt, statusChangedBy: 'admin-1', statusReason: productState.statusReason })
    }),
    http.post(`${adminApi}/products/:id/resume`, () => {
      productState = { ...productState, publicationStatus: 'PUBLISHED', statusReason: null, statusChangedAt: '2026-11-06T12:05:00Z' }
      return HttpResponse.json({ productId: productState.id, publicationStatus: productState.publicationStatus, statusChangedAt: productState.statusChangedAt, statusChangedBy: 'admin-1', statusReason: null })
    }),
  )

  const user = userEvent.setup()
  open('/admin')
  expect(await screen.findByRole('heading', { name: 'Vợt Yonex Lifecycle', level: 2 })).toBeVisible()
  await user.type(screen.getByLabelText('Lý do tạm ngừng'), 'Kiểm tra chất lượng lô hàng')
  await user.click(screen.getByRole('button', { name: 'Tạm ngừng bán' }))
  await user.click(screen.getByRole('button', { name: 'Xác nhận' }))
  expect(await screen.findByText(/Đã chuyển sản phẩm sang SUSPENDED/i)).toBeVisible()
  expect(await screen.findByText(/Sản phẩm đang tạm ngừng bán/i)).toBeVisible()

  await user.click(screen.getByRole('button', { name: 'Mở bán lại' }))
  await user.click(screen.getByRole('button', { name: 'Xác nhận' }))
  expect(await screen.findByText(/Đã chuyển sản phẩm sang PUBLISHED/i)).toBeVisible()
})
