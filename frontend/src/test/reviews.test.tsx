import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined

afterEach(() => {
  router?.dispose()
})

const mockCustomerMe = {
  id: 'cust-id-1',
  email: 'customer@example.com',
  fullName: 'Nguyễn Văn An',
  role: 'CUSTOMER',
}

const mockStaffMe = {
  id: 'staff-id-1',
  email: 'staff@example.com',
  fullName: 'Trần Nhân Sự',
  role: 'STAFF',
}

const mockProduct = {
  id: 'prod-rev-1',
  title: 'Vợt Cầu Lông Yonex Astrox 88D Pro',
  description: 'Dòng vợt cao cấp cho vận động viên tấn công uy lực.',
  category: { id: 'cat-1', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
  skus: [
    { id: 'sku-rev-1', skuCode: 'AX88D-4U5', optionLabel: 'Đỏ / 4U5', price: 3800000, inStock: true },
  ],
  images: [
    { id: 'img-rev-1', fileKey: 'images/ax88d.jpg', altText: 'Yonex Astrox 88D Pro', displayOrder: 1 },
  ],
  averageRating: 4.8,
  reviewCount: 5,
}

const mockPublicReviewsPage = {
  items: [
    {
      id: 'rev-item-1',
      rating: 5,
      content: 'Vợt đánh rất đầm tay, smash uy lực, phông cầu thoát. Rất đáng tiền!',
      createdAt: '2026-11-01T10:00:00Z',
      updatedAt: '2026-11-01T10:00:00Z',
      reviewerDisplayName: 'N***n A*',
      verifiedPurchase: true,
    },
    {
      id: 'rev-item-2',
      rating: 4,
      content: 'Đóng gói cẩn thận, vợt chính hãng, giao hàng đúng hẹn.',
      createdAt: '2026-11-02T14:30:00Z',
      updatedAt: '2026-11-02T14:30:00Z',
      reviewerDisplayName: 'L***e B*',
      verifiedPurchase: true,
    },
  ],
  summary: {
    averageRating: 4.8,
    reviewCount: 5,
    distribution: { one: 0, two: 0, three: 0, four: 1, five: 4 },
  },
  page: 1,
  pageSize: 10,
  totalCount: 5,
}

describe('Product Reviews & Ratings Frontend Tests (T36)', () => {
  test('Lọc sao gửi rating đến API và thay danh sách bằng kết quả đã lọc', async () => {
    let requestedRating: string | null = null
    server.use(
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, ({ request }) => {
        requestedRating = new URL(request.url).searchParams.get('rating')
        const items = mockPublicReviewsPage.items.filter(item => !requestedRating || item.rating === Number(requestedRating))
        return HttpResponse.json({ ...mockPublicReviewsPage, items, totalCount: items.length })
      })
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)
    expect(await screen.findByText('L***e B*')).toBeVisible()
    const user = userEvent.setup()
    const filters = screen.getByRole('group', { name: 'Lọc theo số sao' })
    await user.click(within(filters).getByRole('button', { name: '5 ★' }))
    await waitFor(() => expect(requestedRating).toBe('5'))
    await waitFor(() => expect(screen.queryByText('L***e B*')).not.toBeInTheDocument())
    expect(screen.getByText('N***n A*')).toBeVisible()
    await user.click(within(filters).getByRole('button', { name: 'Tất cả' }))
    expect(await screen.findByText('L***e B*')).toBeVisible()
    expect(requestedRating).toBeNull()
  })

  test('Khách ẩn danh vào trang chi tiết thấy tóm tắt điểm sao, danh sách nhận xét che tên và lời nhắc đăng nhập', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json({ code: 'UNAUTHORIZED' }, { status: 401 })),
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, () => HttpResponse.json(mockPublicReviewsPage))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)

    // Heading and rating badge
    expect(await screen.findByRole('heading', { name: mockProduct.title })).toBeVisible()
    expect(screen.getByText('4.8')).toBeVisible()

    // Reviews section
    expect(await screen.findByRole('heading', { name: /Đánh giá & Nhận xét/i })).toBeVisible()
    expect(await screen.findByText('N***n A*')).toBeVisible()
    expect(screen.getByText(/Vợt đánh rất đầm tay/i)).toBeVisible()
    expect(screen.getByText('L***e B*')).toBeVisible()

    // Login prompt for anonymous user
    expect(screen.getByText(/Vui lòng/i)).toBeVisible()
    expect(screen.getByRole('link', { name: /đăng nhập tài khoản mua hàng/i })).toHaveAttribute('href', '/login')
  })

  test('Customer đăng nhập nhưng chưa có đơn hàng DELIVERED thấy thông báo chỉ người mua mới được đánh giá', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomerMe)),
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, () => HttpResponse.json(mockPublicReviewsPage)),
      http.get(`${root}/products/prod-rev-1/review-eligibility`, () =>
        HttpResponse.json({
          eligible: false,
          reasonCode: 'NO_DELIVERED_ORDER',
          existingReview: null,
        })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: mockProduct.title })).toBeVisible()
    expect(await screen.findByText(/Chỉ khách hàng đã mua và nhận hàng thành công mới có thể đánh giá/i)).toBeVisible()
    expect(screen.queryByRole('button', { name: /Viết đánh giá/i })).not.toBeInTheDocument()
  })

  test('Customer đủ điều kiện mở form, kiểm tra tối thiểu 10 ký tự và gửi đánh giá thành công với CSRF', async () => {
    let capturedBody: Record<string, unknown> | null = null
    let capturedCsrf: string | null = null

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomerMe)),
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, () => HttpResponse.json(mockPublicReviewsPage)),
      http.get(`${root}/products/prod-rev-1/review-eligibility`, () =>
        HttpResponse.json({
          eligible: true,
          reasonCode: null,
          existingReview: null,
        })
      ),
      http.post(`${root}/products/prod-rev-1/reviews`, async ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-TOKEN')
        capturedBody = (await request.json()) as Record<string, unknown>
        return HttpResponse.json({
          id: 'new-rev-123',
          productId: 'prod-rev-1',
          productTitle: mockProduct.title,
          reviewStatus: 'PUBLISHED',
          moderatedAt: null,
          moderationReason: null,
          rating: capturedBody.rating,
          content: capturedBody.content,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        }, { status: 201 })
      })
    )

    const user = userEvent.setup()
    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)

    // Wait for eligible banner
    expect(await screen.findByText(/Bạn đã mua và nhận sản phẩm này!/i)).toBeVisible()
    const openBtn = screen.getByRole('button', { name: /Viết đánh giá/i })
    await user.click(openBtn)

    // Form appears
    expect(await screen.findByRole('heading', { name: /Viết đánh giá sản phẩm/i })).toBeVisible()
    const textarea = screen.getByRole('textbox', { name: /Nội dung nhận xét/i })
    const submitBtn = screen.getByRole('button', { name: /Gửi đánh giá/i })

    // Disabled initially because content is empty (< 10 chars)
    expect(submitBtn).toBeDisabled()

    // Type 5 chars
    await user.type(textarea, 'Vợt xịn')
    expect(submitBtn).toBeDisabled()
    expect(screen.getByText(/Tối thiểu 10 ký tự/i)).toBeVisible()

    // Complete valid review
    await user.type(textarea, ', phông cầu rất thoát tay và smash đầm!')
    expect(submitBtn).toBeEnabled()

    // Select 5 stars
    const fiveStarRadio = screen.getByRole('radio', { name: '5 trên 5 sao' })
    await user.click(fiveStarRadio)

    await user.click(submitBtn)

    await waitFor(() => {
      expect(capturedCsrf).toBe('csrf-test-token')
      expect(capturedBody).toEqual({
        rating: 5,
        content: 'Vợt xịn, phông cầu rất thoát tay và smash đầm!',
      })
    })

    expect(await screen.findByText(/Cảm ơn bạn đã gửi đánh giá sản phẩm!/i)).toBeVisible()
  })

  test('Customer đã có đánh giá có thể mở form Chỉnh sửa và gửi PATCH thành công', async () => {
    let patchedBody: Record<string, unknown> | null = null
    let patchedCsrf: string | null = null

    const existingReview = {
      id: 'my-rev-999',
      productId: 'prod-rev-1',
      productTitle: 'Vợt Cầu Lông Yonex Astrox 88D Pro',
      rating: 4,
      content: 'Ban đầu đánh hơi cứng nhưng sau vài buổi thì rất êm tay.',
      reviewStatus: 'PUBLISHED' as const,
      createdAt: '2026-11-01T08:00:00Z',
      updatedAt: '2026-11-01T08:00:00Z',
      moderatedAt: null,
      moderationReason: null,
    }

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomerMe)),
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, () => HttpResponse.json(mockPublicReviewsPage)),
      http.get(`${root}/products/prod-rev-1/review-eligibility`, () =>
        HttpResponse.json({
          eligible: false,
          reasonCode: 'ALREADY_REVIEWED',
          existingReview,
        })
      ),
      http.patch(`${root}/reviews/my-rev-999`, async ({ request }) => {
        patchedCsrf = request.headers.get('X-CSRF-TOKEN')
        patchedBody = (await request.json()) as Record<string, unknown>
        return HttpResponse.json({
          id: 'my-rev-999',
          productId: 'prod-rev-1',
          productTitle: mockProduct.title,
          reviewStatus: 'PUBLISHED',
          moderatedAt: null,
          moderationReason: null,
          rating: patchedBody.rating,
          content: patchedBody.content,
          createdAt: existingReview.createdAt,
          updatedAt: new Date().toISOString(),
        })
      })
    )

    const user = userEvent.setup()
    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: /Đánh giá của bạn/i })).toBeVisible()
    expect(screen.getByText(/Ban đầu đánh hơi cứng/i)).toBeVisible()

    const editBtn = screen.getByRole('button', { name: /Chỉnh sửa/i })
    await user.click(editBtn)

    expect(await screen.findByRole('heading', { name: /Chỉnh sửa đánh giá của bạn/i })).toBeVisible()
    const textarea = screen.getByRole('textbox', { name: /Nội dung nhận xét/i })
    expect(textarea).toHaveValue(existingReview.content)

    // Edit content and increase rating to 5
    await user.clear(textarea)
    await user.type(textarea, 'Sau 1 tháng sử dụng thì cây vợt này thực sự quá tuyệt vời!')
    const fiveStarRadio = screen.getByRole('radio', { name: '5 trên 5 sao' })
    await user.click(fiveStarRadio)

    const saveBtn = screen.getByRole('button', { name: /Cập nhật đánh giá/i })
    await user.click(saveBtn)

    await waitFor(() => {
      expect(patchedCsrf).toBe('csrf-test-token')
      expect(patchedBody).toEqual({
        rating: 5,
        content: 'Sau 1 tháng sử dụng thì cây vợt này thực sự quá tuyệt vời!',
      })
    })

    expect(await screen.findByText(/Cập nhật đánh giá thành công!/i)).toBeVisible()
  })

  test('Customer xóa đánh giá của mình với xác nhận và gửi DELETE thành công', async () => {
    let deletedId: string | null = null
    let deleteCsrf: string | null = null

    const existingReview = {
      id: 'my-rev-888',
      productId: 'prod-rev-1',
      productTitle: 'Vợt Cầu Lông Yonex Astrox 88D Pro',
      rating: 5,
      content: 'Đánh giá tạm để test xóa đánh giá.',
      reviewStatus: 'PUBLISHED' as const,
      createdAt: '2026-11-01T08:00:00Z',
      updatedAt: '2026-11-01T08:00:00Z',
      moderatedAt: null,
      moderationReason: null,
    }

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomerMe)),
      http.get(`${root}/catalog/products/prod-rev-1`, () => HttpResponse.json(mockProduct)),
      http.get(`${root}/catalog/products/prod-rev-1/reviews`, () => HttpResponse.json(mockPublicReviewsPage)),
      http.get(`${root}/products/prod-rev-1/review-eligibility`, () =>
        HttpResponse.json({
          eligible: false,
          reasonCode: 'ALREADY_REVIEWED',
          existingReview,
        })
      ),
      http.delete(`${root}/reviews/my-rev-888`, ({ request }) => {
        deleteCsrf = request.headers.get('X-CSRF-TOKEN')
        deletedId = 'my-rev-888'
        return new HttpResponse(null, { status: 204 })
      })
    )

    const user = userEvent.setup()
    router = createMemoryRouter(appRoutes, { initialEntries: ['/products/prod-rev-1'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: /Đánh giá của bạn/i })).toBeVisible()

    const deleteBtn = screen.getByRole('button', { name: /^Xóa$/i })
    await user.click(deleteBtn)

    // Confirm dialog appears
    expect(await screen.findByRole('heading', { name: /Xóa đánh giá\?/i })).toBeVisible()
    const confirmBtn = screen.getByRole('button', { name: /Xóa đánh giá/i })
    await user.click(confirmBtn)

    await waitFor(() => {
      expect(deleteCsrf).toBe('csrf-test-token')
      expect(deletedId).toBe('my-rev-888')
    })

    expect(await screen.findByText(/Đã xóa đánh giá của bạn\./i)).toBeVisible()
  })

  test('ProductCard hiển thị số sao; OrdersPage hiển thị nút Đánh giá cho đơn COMPLETED + DELIVERED', async () => {
    const mockOrderPage = {
      items: [
        {
          id: 'ord-del-1',
          orderNumber: 'ORD-20261104-001',
          orderStatus: 'COMPLETED',
          paymentMethod: 'COD',
          paymentStatus: 'PAID',
          reservationStatus: 'ALLOCATED',
          totalAmount: 3800000,
          createdAt: '2026-11-01T00:00:00Z',
          deadlineAt: '2026-11-02T00:00:00Z',
          confirmedAt: '2026-11-01T01:00:00Z',
          closedReason: null,
          recipientName: 'Nguyễn Văn An',
          recipientPhone: '0901234567',
          shippingAddress: '123 Đường Cầu Lông, Hà Nội',
          shipmentId: 'ship-1',
          shipmentStatus: 'DELIVERED',
          shippedAt: '2026-11-01T03:00:00Z',
          deliveredAt: '2026-11-02T10:00:00Z',
          redeliveryCount: 0,
          canCancel: false,
          cancellationReason: null,
          items: [
            {
              productId: 'prod-rev-1',
              productSkuId: 'sku-rev-1',
              productTitle: 'Vợt Cầu Lông Yonex Astrox 88D Pro',
              skuCode: 'AX88D-4U5',
              optionLabel: 'Đỏ / 4U5',
              quantity: 1,
              unitPrice: 3800000,
              lineTotal: 3800000,
            },
          ],
          deliveryAttempts: [],
        },
      ],
      page: 1,
      pageSize: 50,
      totalCount: 1,
    }

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockCustomerMe)),
      http.get(`${root}/orders`, () => HttpResponse.json(mockOrderPage))
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/orders'] })
    render(<RouterProvider router={router} />)

    expect(await screen.findByRole('heading', { name: /Đơn hàng của tôi/i })).toBeVisible()
    const reviewLink = await screen.findByRole('link', { name: /★ Đánh giá sản phẩm/i })
    expect(reviewLink).toBeVisible()
    expect(reviewLink).toHaveAttribute('href', '/products/prod-rev-1#reviews')
  })

  test('Staff mở tab Kiểm duyệt Đánh giá, ẩn đánh giá vi phạm và khôi phục đánh giá thành công', async () => {
    let capturedAction: Record<string, unknown> | null = null

    const mockModerationList = [
      {
        id: 'mod-rev-1',
        productId: 'prod-rev-1',
        productTitle: 'Vợt Cầu Lông Yonex Astrox 88D Pro',
        customerId: 'cust-1',
        customerName: 'Nguyễn Văn A',
        customerEmail: 'a@example.com',
        rating: 1,
        content: 'Shop làm ăn chán quá, liên hệ zalo 0999999 để mua rẻ hơn!',
        reviewStatus: 'PUBLISHED' as const,
        createdAt: '2026-11-03T10:00:00Z',
        updatedAt: '2026-11-03T10:00:00Z',
        moderatedBy: null,
        moderatorName: null,
        moderatedAt: null,
        moderationReason: null,
        deletedAt: null,
      },
    ]

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(mockStaffMe)),
      http.get(`${root}/staff/products`, () => HttpResponse.json([])),
      http.get(`${root}/inventory/summaries`, () => HttpResponse.json([])),
      http.get(`${root}/staff/orders/cod-pending`, () => HttpResponse.json([])),
      http.get(`${root}/staff/fulfillments`, () => HttpResponse.json([])),
      http.get(`${root}/moderation/reviews`, () =>
        HttpResponse.json({
          items: mockModerationList,
          page: 1,
          pageSize: 20,
          totalCount: 1,
        })
      ),
      http.patch(`${root}/moderation/reviews/mod-rev-1`, async ({ request }) => {
        capturedAction = (await request.json()) as Record<string, unknown>
        Object.assign(mockModerationList[0]!, capturedAction.action === 'HIDE' ? {
          reviewStatus: 'HIDDEN', moderatedBy: 'staff-id-1', moderatorName: 'Trần Nhân Sự',
          moderatedAt: new Date().toISOString(), moderationReason: capturedAction.reason,
        } : {
          reviewStatus: 'PUBLISHED', moderatedBy: null, moderatorName: null,
          moderatedAt: null, moderationReason: null,
        })
        return HttpResponse.json(mockModerationList[0])
      })
    )

    const user = userEvent.setup()
    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
    render(<RouterProvider router={router} />)

    // Click on tab Kiểm duyệt Đánh giá
    const tabReviews = await screen.findByRole('button', { name: /Kiểm duyệt Đánh giá/i })
    await user.click(tabReviews)

    expect(await screen.findByRole('heading', { name: /Kiểm duyệt Đánh giá/i })).toBeVisible()
    expect(await screen.findByText('Nguyễn Văn A')).toBeVisible()
    expect(screen.getByText(/Shop làm ăn chán quá/i)).toBeVisible()

    // Click Ẩn review
    const hideBtn = screen.getByRole('button', { name: /^Ẩn$/i })
    await user.click(hideBtn)

    // Modal appears
    expect(await screen.findByRole('heading', { name: /Ẩn đánh giá của khách hàng/i })).toBeVisible()
    const reasonInput = screen.getByRole('textbox', { name: /Lý do ẩn đánh giá/i })
    const confirmHideBtn = screen.getByRole('button', { name: /Xác nhận ẩn/i })

    // Disabled when empty
    expect(confirmHideBtn).toBeDisabled()

    await user.type(reasonInput, 'Spam quảng cáo số điện thoại lạ')
    expect(confirmHideBtn).toBeEnabled()

    await user.click(confirmHideBtn)

    await waitFor(() => {
      expect(capturedAction).toEqual({
        action: 'HIDE',
        reason: 'Spam quảng cáo số điện thoại lạ',
      })
    })

    expect(await screen.findByText(/Đã ẩn đánh giá của khách hàng "Nguyễn Văn A"\./i)).toBeVisible()
    await user.click(await screen.findByRole('button', { name: /^Khôi phục$/ }))
    const dialog = screen.getByRole('dialog', { name: 'Khôi phục đánh giá sản phẩm' })
    await user.click(within(dialog).getByRole('button', { name: 'Khôi phục' }))
    expect(await screen.findByText(/Đã khôi phục hiển thị đánh giá của "Nguyễn Văn A"/)).toBeVisible()
    expect(capturedAction).toEqual({ action: 'RESTORE' })
    expect(await screen.findByRole('button', { name: /^Ẩn$/ })).toBeVisible()
  })
})
