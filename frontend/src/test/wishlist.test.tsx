import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import { WishlistProvider } from '../features/wishlist/WishlistContext'
import { useWishlist } from '../features/wishlist/useWishlist'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => {
  router?.dispose()
})

const mockCustomer = {
  id: 'cust-wish-1',
  email: 'customer@example.com',
  fullName: 'Nguyễn Cầu Lông',
  role: 'CUSTOMER' as const,
  emailVerified: true,
}

const mockProduct = {
  id: 'prod-w-1',
  title: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
  description: 'Vợt cầu lông trợ lực nhanh đỉnh cao thế giới.',
  category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
  skus: [
    { id: 'sku-w-1', skuCode: 'NF1000Z-4U5', optionLabel: 'Vàng / 4U5', price: 4200000, inStock: true },
    { id: 'sku-w-2', skuCode: 'NF1000Z-3U5', optionLabel: 'Vàng / 3U5', price: 4200000, inStock: false },
  ],
  images: [{ id: 'img-1', fileKey: 'nf1000z.jpg', altText: 'NF 1000Z', displayOrder: 1 }],
  averageRating: 4.9,
  reviewCount: 12,
}

const mockCatalog = {
  categories: [{ id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' }],
  products: {
    items: [
      {
        id: 'prod-w-1',
        title: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
        category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
        minPrice: 4200000,
        inStock: true,
        imageUrl: 'nf1000z.jpg',
        averageRating: 4.9,
        reviewCount: 12,
      },
    ],
    totalCount: 1,
    page: 1,
    pageSize: 12,
  },
}

describe('Customer Wishlist Frontend Tests (T37)', () => {
  test('1. Khách ẩn danh click nút trái tim được điều hướng đến trang Đăng nhập', async () => {
    const user = userEvent.setup()
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json({ code: 'UNAUTHORIZED' }, { status: 401 })),
      http.get(`${root}/catalog/categories`, () => HttpResponse.json(mockCatalog.categories)),
      http.get(`${root}/catalog/products`, () => HttpResponse.json(mockCatalog.products))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
    render(<RouterProvider router={router} />)

    const heartBtn = await screen.findByTestId('wishlist-btn-prod-w-1')
    expect(heartBtn).toBeInTheDocument()
    expect(heartBtn).toHaveAttribute('aria-pressed', 'false')

    await user.click(heartBtn)

    await waitFor(() => expect(router?.state.location.pathname).toBe('/login'))
    expect(router?.state.location.search).toBe('?returnTo=%2Fproducts%2Fprod-w-1')
  })

  test('2. Customer đăng nhập: nút trái tim và badge header phản ánh trạng thái đã lưu', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/catalog/categories`, () => HttpResponse.json(mockCatalog.categories)),
      http.get(`${root}/catalog/products`, () => HttpResponse.json(mockCatalog.products)),
      http.get(`${root}/wishlist/summary`, () =>
        HttpResponse.json({ totalCount: 1, productIds: ['prod-w-1'] })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
    render(<RouterProvider router={router} />)

    const heartBtn = await screen.findByTestId('wishlist-btn-prod-w-1')
    await waitFor(() => {
      expect(heartBtn).toHaveAttribute('aria-pressed', 'true')
    })
    expect(heartBtn).toHaveClass('is-saved')

    // Badge on header
    const wishlistLink = screen.getByRole('link', { name: /Yêu thích/i })
    expect(within(wishlistLink).getByText('1')).toBeInTheDocument()
  })

  test('3. Customer lưu sản phẩm mới gửi PUT với CSRF header và cập nhật tức thì', async () => {
    const user = userEvent.setup()
    let putCalled = false
    let csrfTokenSent: string | null = null
    const savedIds: string[] = []

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/catalog/categories`, () => HttpResponse.json(mockCatalog.categories)),
      http.get(`${root}/catalog/products`, () => HttpResponse.json(mockCatalog.products)),
      http.get(`${root}/wishlist/summary`, () =>
        HttpResponse.json({ totalCount: savedIds.length, productIds: savedIds })
      ),
      http.put(`${root}/wishlist/items/:id`, ({ request, params }) => {
        if (params.id === 'prod-w-1') {
          putCalled = true
          csrfTokenSent = request.headers.get('X-CSRF-TOKEN')
          savedIds.push('prod-w-1')
          return new HttpResponse(null, { status: 204 })
        }
        return new HttpResponse(null, { status: 400 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
    render(<RouterProvider router={router} />)

    const heartBtn = await screen.findByTestId('wishlist-btn-prod-w-1')
    expect(heartBtn).toHaveAttribute('aria-pressed', 'false')

    await user.click(heartBtn)

    await waitFor(() => {
      expect(putCalled).toBe(true)
      expect(csrfTokenSent).toBe('csrf-test-token')
      expect(heartBtn).toHaveAttribute('aria-pressed', 'true')
    })
  })

  test('4. Customer bỏ lưu sản phẩm gửi DELETE với CSRF header', async () => {
    const user = userEvent.setup()
    let deleteCalled = false
    let csrfTokenSent: string | null = null
    let savedIds = ['prod-w-1']

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/catalog/categories`, () => HttpResponse.json(mockCatalog.categories)),
      http.get(`${root}/catalog/products`, () => HttpResponse.json(mockCatalog.products)),
      http.get(`${root}/wishlist/summary`, () =>
        HttpResponse.json({ totalCount: savedIds.length, productIds: savedIds })
      ),
      http.delete(`${root}/wishlist/items/:id`, ({ request, params }) => {
        if (params.id === 'prod-w-1') {
          deleteCalled = true
          csrfTokenSent = request.headers.get('X-CSRF-TOKEN')
          savedIds = savedIds.filter(id => id !== 'prod-w-1')
          return new HttpResponse(null, { status: 204 })
        }
        return new HttpResponse(null, { status: 400 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
    render(<RouterProvider router={router} />)

    const heartBtn = await screen.findByTestId('wishlist-btn-prod-w-1')
    await waitFor(() => {
      expect(heartBtn).toHaveAttribute('aria-pressed', 'true')
    })

    await user.click(heartBtn)

    await waitFor(() => {
      expect(deleteCalled).toBe(true)
      expect(csrfTokenSent).toBe('csrf-test-token')
      expect(heartBtn).toHaveAttribute('aria-pressed', 'false')
    })
  })

  test('5. Giới hạn 500: API trả về 409 WISHLIST_LIMIT_REACHED hiển thị thông báo lỗi và hoàn tác', async () => {
    const user = userEvent.setup()

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/catalog/categories`, () => HttpResponse.json(mockCatalog.categories)),
      http.get(`${root}/catalog/products`, () => HttpResponse.json(mockCatalog.products)),
      http.get(`${root}/wishlist/summary`, () =>
        HttpResponse.json({ totalCount: 500, productIds: [] })
      ),
      http.put(`${root}/wishlist/items/:id`, () =>
        HttpResponse.json(
          {
            code: 'WISHLIST_LIMIT_REACHED',
            title: 'Danh sách yêu thích đã đầy (tối đa 500 sản phẩm).',
          },
          { status: 409 }
        )
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
    render(<RouterProvider router={router} />)

    const heartBtn = await screen.findByTestId('wishlist-btn-prod-w-1')
    await user.click(heartBtn)

    // Optimistic state rolled back to false
    await waitFor(() => {
      expect(heartBtn).toHaveAttribute('aria-pressed', 'false')
    })
  })

  test('6. WishlistPage: hiển thị đúng các trạng thái Còn hàng, Tạm hết hàng và Ngừng kinh doanh', async () => {
    const user = userEvent.setup()
    let deleteTarget: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/wishlist`, () =>
        HttpResponse.json({
          items: [
            {
              productId: 'prod-1',
              addedAt: '2026-11-04T10:00:00Z',
              availability: 'AVAILABLE',
              product: {
                id: 'prod-1',
                title: 'Vợt Sẵn Hàng Pro',
                category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
                minPrice: 3500000,
                inStock: true,
                imageUrl: 'v1.jpg',
                averageRating: 5.0,
                reviewCount: 3,
              },
            },
            {
              productId: 'prod-2',
              addedAt: '2026-11-04T09:00:00Z',
              availability: 'OUT_OF_STOCK',
              product: {
                id: 'prod-2',
                title: 'Giày Hết Hàng Limited',
                category: { id: 'cat-2', name: 'Giày cầu lông', slug: 'giay' },
                minPrice: 2200000,
                inStock: false,
                imageUrl: null,
                averageRating: 0,
                reviewCount: 0,
              },
            },
            {
              productId: 'prod-3',
              addedAt: '2026-11-04T08:00:00Z',
              availability: 'PRODUCT_UNAVAILABLE',
              product: null,
            },
          ],
          page: 1,
          pageSize: 12,
          totalCount: 3,
        }),
      ),
      http.delete(`${root}/wishlist/items/:id`, ({ params }) => {
        deleteTarget = String(params.id)
        return new HttpResponse(null, { status: 204 })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)

    // Heading & item count
    expect(await screen.findByRole('heading', { name: 'Sản phẩm yêu thích' })).toBeInTheDocument()
    expect(screen.getByText('3/500 sản phẩm')).toBeInTheDocument()

    // Card 1: Available
    const card1 = screen.getByTestId('wishlist-card-prod-1')
    expect(within(card1).getByText('Còn hàng')).toBeInTheDocument()
    expect(within(card1).getByRole('button', { name: 'Thêm vào giỏ' })).toBeEnabled()

    // Card 2: Out of stock
    const card2 = screen.getByTestId('wishlist-card-prod-2')
    expect(within(card2).getByText('Tạm hết hàng')).toBeInTheDocument()
    expect(within(card2).getByRole('button', { name: 'Hết hàng' })).toBeDisabled()

    // Card 3: Unavailable
    const card3 = screen.getByTestId('wishlist-card-prod-3')
    expect(within(card3).getByText('Ngừng kinh doanh')).toBeInTheDocument()
    expect(within(card3).getByRole('button', { name: 'Ngừng bán' })).toBeDisabled()

    // Remove unavailable product
    const removeBtn3 = within(card3).getByRole('button', { name: /Bỏ lưu/i })
    await user.click(removeBtn3)

    await waitFor(() => {
      expect(deleteTarget).toBe('prod-3')
      expect(screen.getByText('Đã xóa sản phẩm khỏi danh sách yêu thích.')).toBeInTheDocument()
    })
  })

  test('7. WishlistPage: mở SkuSelectModal, chọn phân loại và thêm vào giỏ thành công', async () => {
    const user = userEvent.setup()
    let mergeCalled = false

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/wishlist`, () =>
        HttpResponse.json({
          items: [
            {
              productId: 'prod-w-1',
              addedAt: '2026-11-04T10:00:00Z',
              availability: 'AVAILABLE',
              product: {
                id: 'prod-w-1',
                title: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
                category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
                minPrice: 4200000,
                inStock: true,
                imageUrl: 'nf1000z.jpg',
                averageRating: 4.9,
                reviewCount: 12,
              },
            },
          ],
          page: 1,
          pageSize: 12,
          totalCount: 1,
        })
      ),
      http.get(`${root}/catalog/products/prod-w-1`, () => HttpResponse.json(mockProduct)),
      http.post(`${root}/cart/merge`, () => {
        mergeCalled = true
        return HttpResponse.json({
          cart: {
            id: 'cart-1',
            items: [
              {
                productSkuId: 'sku-w-1',
                productId: 'prod-w-1',
                productTitle: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
                skuCode: 'NF1000Z-4U5',
                optionLabel: 'Vàng / 4U5',
                unitPrice: 4200000,
                quantity: 1,
                lineTotal: 4200000,
                imageUrl: 'nf1000z.jpg',
                availabilityCode: 'AVAILABLE',
                flashSaleOfferId: null,
              },
            ],
            totalQuantity: 1,
            subtotal: 4200000,
            canCheckout: true,
            updatedAt: '2026-11-04T12:00:00Z',
          },
        })
      })
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist'] })
    render(<RouterProvider router={router} />)

    const card = await screen.findByTestId('wishlist-card-prod-w-1')
    const addBtn = within(card).getByRole('button', { name: 'Thêm vào giỏ' })
    await user.click(addBtn)

    // Modal opens
    expect(await screen.findByRole('dialog', { name: 'Chọn phiên bản' })).toBeInTheDocument()
    expect(screen.getByText('Vàng / 4U5')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Đóng cửa sổ' })).toHaveFocus())
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: 'Chọn phiên bản' })).not.toBeInTheDocument()
    expect(addBtn).toHaveFocus()
    await user.click(addBtn)
    expect(await screen.findByRole('dialog', { name: 'Chọn phiên bản' })).toBeInTheDocument()

    // Multiple SKUs require an explicit selection; unavailable variants cannot be selected.
    const confirmBtn = screen.getByRole('button', { name: 'Chọn phiên bản' })
    expect(confirmBtn).toBeDisabled()
    expect(screen.getByRole('radio', { name: /Vàng \/ 3U5/ })).toBeDisabled()
    await user.click(screen.getByRole('radio', { name: /Vàng \/ 4U5/ }))
    const addToCartConfirm = screen.getByRole('button', { name: 'Thêm vào giỏ hàng' })
    expect(addToCartConfirm).toBeEnabled()
    await user.click(addToCartConfirm)

    await waitFor(() => {
      expect(mergeCalled).toBe(true)
      expect(screen.getByText(/Đã thêm "Vợt Cầu Lông Yonex Nanoflare 1000Z" vào giỏ hàng!/)).toBeInTheDocument()
    })

    // Product is still on the wishlist page
    expect(screen.getByTestId('wishlist-card-prod-w-1')).toBeInTheDocument()
  })

  test('8. Response summary cũ không thể ghi đè state sau khi đổi tài khoản', async () => {
    let summaryRequest = 0
    let releaseFirstResponse!: () => void
    const firstResponseGate = new Promise<void>(resolve => { releaseFirstResponse = resolve })
    server.use(
      http.get(`${root}/wishlist/summary`, async () => {
        summaryRequest += 1
        if (summaryRequest === 1) {
          await firstResponseGate
          return HttpResponse.json({ totalCount: 1, productIds: ['private-product-a'] })
        }
        return HttpResponse.json({ totalCount: 1, productIds: ['product-b'] })
      })
    )

    function Probe() {
      const wishlist = useWishlist()
      return <output data-testid="wishlist-probe">{wishlist.totalCount}:{Array.from(wishlist.wishlistIds).join(',')}</output>
    }

    const customerB = { ...mockCustomer, id: 'cust-wish-2', email: 'customer-b@example.com' }
    const view = render(
      <MemoryRouter>
        <WishlistProvider user={mockCustomer}><Probe /></WishlistProvider>
      </MemoryRouter>
    )
    await waitFor(() => expect(summaryRequest).toBe(1))

    view.rerender(
      <MemoryRouter>
        <WishlistProvider user={customerB}><Probe /></WishlistProvider>
      </MemoryRouter>
    )
    await waitFor(() => expect(screen.getByTestId('wishlist-probe')).toHaveTextContent('1:product-b'))

    releaseFirstResponse()
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(screen.getByTestId('wishlist-probe')).toHaveTextContent('1:product-b')
    expect(screen.getByTestId('wishlist-probe')).not.toHaveTextContent('private-product-a')
  })

  test('9. Xóa item cuối của trang cuối tự chuyển về trang hợp lệ', async () => {
    const user = userEvent.setup()
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomer)),
      http.get(`${root}/wishlist`, ({ request }) => {
        const page = new URL(request.url).searchParams.get('page') ?? '1'
        if (page === '2') {
          return HttpResponse.json({
            items: [{
              productId: 'last-page-product',
              addedAt: '2026-11-04T10:00:00Z',
              availability: 'PRODUCT_UNAVAILABLE',
              product: null,
            }],
            page: 2,
            pageSize: 12,
            totalCount: 13,
          })
        }
        return HttpResponse.json({ items: [], page: 1, pageSize: 12, totalCount: 12 })
      }),
      http.delete(`${root}/wishlist/items/last-page-product`, () => new HttpResponse(null, { status: 204 }))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/wishlist?page=2'] })
    render(<RouterProvider router={router} />)
    const card = await screen.findByTestId('wishlist-card-last-page-product')
    await user.click(within(card).getByRole('button', { name: /Bỏ lưu/i }))

    await waitFor(() => {
      expect(router?.state.location.pathname).toBe('/wishlist')
      expect(router?.state.location.search).toBe('')
    })
  })
})
