import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { delay, http, HttpResponse } from 'msw'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const category = { id: 'cat-1', name: 'Màn hình', slug: 'man-hinh' }
const product = { id: 'product-1', title: 'Màn hình Alpha', category, minPrice: 2500000, inStock: true }
const api = 'http://localhost:5173/api/v1/catalog'
let router: ReturnType<typeof createMemoryRouter> | undefined
function open(path = '/') {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}
beforeEach(() => {
  server.use(
    http.get(`${api}/categories`, () => HttpResponse.json([category])),
    http.get(`${api}/products`, () => HttpResponse.json({ items: [product], totalCount: 1, page: 1, pageSize: 12 })),
    http.get(`${api}/products/product-1`, () => HttpResponse.json({
      id: product.id, title: product.title, category, description: '<script>alert(1)</script>',
      skus: [
        { id: 'sku-1', skuCode: 'ALPHA-A', optionLabel: 'Tiêu chuẩn', price: 2500000, inStock: false },
        { id: 'sku-2', skuCode: 'ALPHA-B', optionLabel: 'Nâng cấp', price: 3500000, inStock: true },
      ],
    })),
  )
})
afterEach(() => router?.dispose())

test('hiển thị catalog từ API và mở chi tiết với tồn của SKU đã chọn', async () => {
  const user = userEvent.setup()
  open()
  await user.click(await screen.findByRole('link', { name: 'Màn hình Alpha' }))
  expect(await screen.findByRole('heading', { name: product.title, level: 1 })).toBeVisible()
  expect(screen.getByText('Tạm hết hàng')).toBeVisible()
  await user.click(screen.getByRole('radio', { name: 'Nâng cấp' }))
  expect(screen.getByText('Còn hàng')).toBeVisible()
  expect(screen.getByText('<script>alert(1)</script>')).toBeVisible()
  expect(document.querySelector('main script')).toBeNull()
})

test('bộ lọc được gửi lên API, lưu trong URL và phân trang giữ bộ lọc', async () => {
  const requests: URL[] = []
  server.use(http.get(`${api}/products`, ({ request }) => {
    const url = new URL(request.url)
    requests.push(url)
    return HttpResponse.json({ items: [product], totalCount: 25, page: Number(url.searchParams.get('page') ?? 1), pageSize: 12 })
  }))
  const user = userEvent.setup()
  const route = open('/?q=ban-dau')
  const search = await screen.findByRole('searchbox', { name: 'Tìm sản phẩm' })
  expect(search).toHaveValue('ban-dau')
  await user.clear(search)
  await user.type(search, 'Alpha')
  await user.selectOptions(screen.getByLabelText('Danh mục'), 'cat-1')
  await user.type(screen.getByLabelText('Giá từ (₫)'), '1000000')
  await user.selectOptions(screen.getByLabelText('Sắp xếp'), 'price-asc')
  await user.click(screen.getByRole('button', { name: 'Áp dụng' }))
  await waitFor(() => expect(new URLSearchParams(route.state.location.search).get('q')).toBe('Alpha'))
  await user.click(await screen.findByRole('link', { name: 'Trang sau' }))
  await waitFor(() => expect(new URLSearchParams(route.state.location.search).get('page')).toBe('2'))
  expect(requests.at(-1)?.searchParams.get('categoryId')).toBe('cat-1')
  expect(requests.at(-1)?.searchParams.get('minPrice')).toBe('1000000')
  expect(requests.at(-1)?.searchParams.get('sort')).toBe('price-asc')
})

test('catalog rỗng không tự thay bằng dữ liệu mẫu', async () => {
  server.use(http.get(`${api}/products`, () => HttpResponse.json({ items: [], totalCount: 0, page: 1, pageSize: 12 })))
  open()
  expect(await screen.findByText('Bộ sưu tập đang được cập nhật')).toBeVisible()
  expect(screen.queryAllByRole('article')).toHaveLength(0)
})

test('lỗi server cho phép thử lại và không hiển thị nội dung kỹ thuật', async () => {
  server.use(http.get(`${api}/products`, () => HttpResponse.json({ title: 'secret connection string', code: 'SERVER_ERROR', correlationId: 'trace-123' }, { status: 500 })))
  const user = userEvent.setup()
  open()
  expect(await screen.findByRole('alert')).toHaveTextContent('Không thể tải sản phẩm')
  expect(screen.queryByText('secret connection string')).toBeNull()
  server.use(http.get(`${api}/products`, () => HttpResponse.json({ items: [product], totalCount: 1, page: 1, pageSize: 12 })))
  await user.click(screen.getByRole('button', { name: 'Thử lại' }))
  expect(await screen.findByRole('link', { name: product.title })).toBeVisible()
})

test('chi tiết không tồn tại có thông báo 404', async () => {
  server.use(http.get(`${api}/products/missing`, () => HttpResponse.json({ code: 'NOT_FOUND' }, { status: 404 })))
  open('/products/missing')
  expect(await screen.findByRole('alert')).toHaveTextContent('Không tìm thấy sản phẩm')
})

test('response sai cấu trúc được xử lý thành lỗi có thể thử lại', async () => {
  server.use(http.get(`${api}/products`, () => HttpResponse.json({ items: 'broken' })))
  open()
  expect(await screen.findByRole('alert')).toHaveTextContent('Không thể tải sản phẩm')
  expect(screen.getByRole('button', { name: 'Thử lại' })).toBeVisible()
})

test('hiển thị trạng thái tải trước khi catalog trả về', async () => {
  server.use(http.get(`${api}/products`, async () => {
    await delay(150)
    return HttpResponse.json({ items: [product], totalCount: 1, page: 1, pageSize: 12 })
  }))
  open()
  expect(screen.getByRole('status')).toHaveTextContent('Đang tải cửa hàng')
  expect(await screen.findByRole('link', { name: product.title })).toBeVisible()
})

test('bộ lọc sai từ URL cho phép trở về danh sách không lọc', async () => {
  server.use(http.get(`${api}/products`, ({ request }) => {
    if (new URL(request.url).searchParams.has('minPrice')) return HttpResponse.json({ code: 'VALIDATION_ERROR' }, { status: 400 })
    return HttpResponse.json({ items: [product], totalCount: 1, page: 1, pageSize: 12 })
  }))
  const user = userEvent.setup()
  open('/?minPrice=-1')
  expect(await screen.findByRole('alert')).toHaveTextContent('Bộ lọc không hợp lệ')
  await user.click(screen.getByRole('link', { name: 'Về cửa hàng / Xóa bộ lọc' }))
  expect(await screen.findByRole('link', { name: product.title })).toBeVisible()
})

test('hiển thị ảnh sản phẩm thật trên danh sách và thư viện ảnh trên trang chi tiết', async () => {
  const productWithImage = {
    ...product,
    imageUrl: 'images/alpha-real.webp',
  }
  server.use(
    http.get(`${api}/products`, () => HttpResponse.json({ items: [productWithImage], totalCount: 1, page: 1, pageSize: 12 })),
    http.get(`${api}/products/product-1`, () => HttpResponse.json({
      id: product.id,
      title: product.title,
      category,
      description: 'Mô tả vợt chất lượng cao',
      skus: [
        { id: 'sku-1', skuCode: 'ALPHA-A', optionLabel: 'Tiêu chuẩn', price: 2500000, inStock: true },
      ],
      images: [
        { id: 'img-1', fileKey: 'images/alpha-front.webp', altText: 'Mặt trước', displayOrder: 1 },
        { id: 'img-2', fileKey: 'images/alpha-back.webp', altText: 'Mặt sau', displayOrder: 2 },
      ],
    })),
  )

  const user = userEvent.setup()
  open()

  // 1. Kiểm tra ảnh ở card sản phẩm
  const cardImg = await screen.findByRole('img', { name: product.title })
  expect(cardImg).toHaveAttribute('src', '/images/alpha-real.webp')

  // 2. Mở trang chi tiết
  await user.click(screen.getByRole('link', { name: product.title }))
  expect(await screen.findByRole('heading', { name: product.title, level: 1 })).toBeVisible()

  // 3. Kiểm tra ảnh chính và thumbnails
  const mainImg = screen.getByRole('img', { name: 'Mặt trước' })
  expect(mainImg).toHaveAttribute('src', '/images/alpha-front.webp')

  const thumbBack = screen.getByRole('button', { name: 'Xem ảnh 2' })
  expect(thumbBack).toBeVisible()
  await user.click(thumbBack)

  // Sau khi click thumb 2, ảnh chính đổi sang ảnh thứ 2
  const updatedMainImg = screen.getByRole('img', { name: 'Mặt sau' })
  expect(updatedMainImg).toHaveAttribute('src', '/images/alpha-back.webp')
})

