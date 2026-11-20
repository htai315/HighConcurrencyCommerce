import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { StaffProductRevisionPanel } from '../features/product-revisions/StaffProductRevisionPanel'
import { AdminProductRevisionsPanel } from '../features/product-revisions/AdminProductRevisionsPanel'
import type { ProductRevision } from '../features/product-revisions/product-revisions.api'
import type { ManagedProduct } from '../features/product-management/product-management.api'
import { server } from './setup'

const api = 'http://localhost:5173/api/v1'
const product: ManagedProduct = { id: 'product-1', title: 'Tên live', description: 'Live', categoryId: null, brandId: null, publicationStatus: 'PUBLISHED', createdAt: '2026-11-06T00:00:00Z', publishedAt: '2026-11-06T00:00:00Z', skus: [], images: [], specifications: [] }
const revision = { id: 'revision-1', productId: 'product-1', revisionNo: 1, revisionStatus: 'DRAFT', baseContentVersion: 1, title: 'Tên live', description: 'Live', categoryId: null, brandId: null, createdBy: 'staff-1', createdAt: '2026-11-06T00:00:00Z', submittedAt: null, reviewedAt: null, reviewNote: null, images: [], specifications: [] }

test('Staff tạo, sửa và gửi revision nhưng không sửa trực tiếp Product live', async () => {
  let open: ProductRevision | null = null
  server.use(
    http.get(`${api}/staff/products/product-1/revisions/open`, () => open ? HttpResponse.json(open) : new HttpResponse(null, { status: 404 })),
    http.get(`${api}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf' })),
    http.post(`${api}/staff/products/product-1/revisions`, () => { open = { ...revision }; return HttpResponse.json(open, { status: 201 }) }),
    http.patch(`${api}/staff/product-revisions/revision-1`, async ({ request }) => { const body = await request.json() as { title: string }; open = { ...revision, title: body.title }; return HttpResponse.json(open) }),
    http.post(`${api}/staff/product-revisions/revision-1/submit`, () => { open = { ...(open ?? revision), revisionStatus: 'PENDING_REVIEW', submittedAt: '2026-11-06T01:00:00Z' }; return HttpResponse.json(open) }),
  )
  const user = userEvent.setup(); render(<StaffProductRevisionPanel product={product} categories={[]} brands={[]} />)
  await user.click(await screen.findByRole('button', { name: 'Tạo bản chỉnh sửa' }))
  const title = await screen.findByLabelText('Tiêu đề đề xuất'); await user.clear(title); await user.type(title, 'Tên đề xuất mới')
  await user.click(screen.getByRole('button', { name: 'Lưu revision' }))
  expect(await screen.findByText('Đã lưu bản chỉnh sửa.')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Gửi duyệt' }))
  expect(await screen.findByText('Đã gửi Admin duyệt.')).toBeVisible()
  expect(title).toBeDisabled()
})

test('Staff giữ nguyên dữ liệu thông số khi API lưu revision thất bại', async () => {
  server.use(
    http.get(`${api}/staff/products/product-1/revisions/open`, () => HttpResponse.json(revision)),
    http.get(`${api}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf' })),
    http.put(`${api}/staff/product-revisions/revision-1/specifications/trong_luong`, () =>
      HttpResponse.json({ code: 'CONCURRENT_CONFLICT', title: 'Không thể lưu thông số.' }, { status: 409 })),
  )

  const user = userEvent.setup()
  render(<StaffProductRevisionPanel product={product} categories={[]} brands={[]} />)

  const key = await screen.findByLabelText('Khóa thông số revision')
  const displayName = screen.getByLabelText('Tên thông số revision')
  const value = screen.getByLabelText('Giá trị thông số revision')
  await user.type(key, 'trong_luong')
  await user.type(displayName, 'Trọng lượng')
  await user.type(value, '4U')
  await user.click(screen.getByRole('button', { name: 'Thêm thông số' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('API request failed (409)')
  expect(key).toHaveValue('trong_luong')
  expect(displayName).toHaveValue('Trọng lượng')
  expect(value).toHaveValue('4U')
})

test('Admin tải queue, xem snapshot và approve revision', async () => {
  let pending = true
  server.use(
    http.get(`${api}/admin/product-revisions`, () => HttpResponse.json(pending ? [{ id: 'revision-1', productId: 'product-1', revisionNo: 1, revisionStatus: 'PENDING_REVIEW', productTitle: 'Tên live', baseContentVersion: 1, createdAt: revision.createdAt, submittedAt: '2026-11-06T01:00:00Z', reviewNote: null }] : [])),
    http.get(`${api}/admin/product-revisions/revision-1/review`, () => HttpResponse.json({ revisionId: 'revision-1', productId: 'product-1', revisionNo: 1, revisionStatus: 'PENDING_REVIEW', productStatus: 'PUBLISHED', baseContentVersion: 1, currentContentVersion: 1, isStale: false, canApprove: true, content: [{ field: 'title', label: 'Tiêu đề', currentValue: 'Tên live', proposedValue: 'Tên đề xuất mới', changed: true }], images: [], specifications: [] })),
    http.get(`${api}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf' })),
    http.post(`${api}/admin/product-revisions/revision-1/approve`, () => { pending = false; return HttpResponse.json({ ...revision, revisionStatus: 'APPROVED' }) }),
  )
  const user = userEvent.setup(); render(<AdminProductRevisionsPanel />)
  await user.click(await screen.findByRole('button', { name: /Tên live/ }))
  expect(await screen.findByText('Tên đề xuất mới')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Duyệt thay đổi' }))
  expect(await screen.findByText('Đã duyệt và cập nhật Storefront.')).toBeVisible()
  await waitFor(() => expect(screen.getByText('Không có bản chỉnh sửa đang chờ.')).toBeVisible())
})

test('Admin thấy stale guard, cleanup placeholder và diff theo ba tab', async () => {
  server.use(
    http.get(`${api}/admin/product-revisions`, () => HttpResponse.json([{ id: 'revision-stale', productId: 'product-1', revisionNo: 2, revisionStatus: 'PENDING_REVIEW', productTitle: 'Tên live', baseContentVersion: 1, createdAt: revision.createdAt, submittedAt: '2026-11-06T01:00:00Z', reviewNote: null }])),
    http.get(`${api}/admin/product-revisions/revision-stale/review`, () => HttpResponse.json({ revisionId: 'revision-stale', productId: 'product-1', revisionNo: 2, revisionStatus: 'PENDING_REVIEW', productStatus: 'PUBLISHED', baseContentVersion: 1, currentContentVersion: 2, isStale: true, canApprove: false, content: [{ field: 'title', label: 'Tiêu đề', currentValue: 'Tên live', proposedValue: 'Tên stale', changed: true }], images: [{ liveImageId: null, revisionImageId: 'image-1', changeType: 'ADDED', currentFileKey: null, proposedFileKey: '/api/v1/media/products/x.png', currentAltText: null, proposedAltText: 'Ảnh stale', currentDisplayOrder: null, proposedDisplayOrder: 0, cleanupStatus: 'COMPLETED', previewAvailable: false }], specifications: [{ key: 'weight', changeType: 'ADDED', currentDisplayName: null, proposedDisplayName: 'Trọng lượng', currentValue: null, proposedValue: '4U', currentDisplayOrder: null, proposedDisplayOrder: 0 }] })),
  )
  const user = userEvent.setup(); render(<AdminProductRevisionsPanel />)
  await user.click(await screen.findByRole('button', { name: /Tên live/ }))
  expect(await screen.findByText(/Revision đã lỗi thời/)).toBeVisible()
  expect(screen.getByRole('button', { name: 'Duyệt thay đổi' })).toBeDisabled()
  await user.click(screen.getByRole('tab', { name: 'Hình ảnh' }))
  expect(screen.getByText('Ảnh đã được dọn khỏi storage')).toBeVisible()
  await user.click(screen.getByRole('tab', { name: 'Thông số' }))
  expect(screen.getByText('4U')).toBeVisible()
})
