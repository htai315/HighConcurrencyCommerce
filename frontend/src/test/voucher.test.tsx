import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { finishSignIn, type AuthUser } from '../features/auth/auth.api'
import {
  parseAdminVoucherSummary,
  parseVoucherPreview,
  VOUCHER_ERROR_MESSAGES,
  type AdminVoucherSummary,
} from '../features/vouchers/voucher.api'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'

const customer: AuthUser = {
  id: 'customer-voucher',
  email: 'customer@example.invalid',
  fullName: 'Khách hàng Cầu Lông',
  role: 'CUSTOMER',
  emailVerified: true,
}

const admin: AuthUser = {
  id: 'admin-voucher',
  email: 'admin@example.invalid',
  fullName: 'Quản trị viên',
  role: 'ADMIN',
  emailVerified: true,
}

const sampleCart = {
  id: 'cart-vch',
  items: [
    {
      productSkuId: 'sku-vch-1',
      productId: 'prod-vch-1',
      productTitle: 'Vợt Pro Carbon',
      skuCode: 'VCH-PRO-4U',
      optionLabel: '4U · G5',
      unitPrice: 500000,
      quantity: 2,
      lineTotal: 1000000,
      imageUrl: null,
      availabilityCode: 'AVAILABLE',
    },
  ],
  totalQuantity: 2,
  subtotal: 1000000,
  canCheckout: true,
  updatedAt: '2026-11-04T00:00:00Z',
}

const sampleAddress = {
  id: 'addr-vch',
  recipientName: 'Nguyễn Văn Test',
  phone: '0901234567',
  addressLine: '123 Đường Cầu Lông',
  ward: 'Phường 1',
  province: 'Hà Nội',
  isDefault: true,
  createdAt: '2026-11-04T00:00:00Z',
}

const sampleVouchers: AdminVoucherSummary[] = [
  {
    id: 'vch-1',
    code: 'GIAM50K',
    name: 'Khuyến mãi mùa giải',
    discountType: 'FIXED_AMOUNT',
    discountValue: 50000,
    voucherStatus: 'ACTIVE',
    minimumOrderAmount: 200000,
    maxDiscountAmount: null,
    usageLimitTotal: 100,
    usageLimitPerCustomer: 1,
    usageHeld: 2,
    usageConsumed: 10,
    totalBudgetAmount: 5000000,
    budgetHeld: 100000,
    budgetConsumed: 500000,
    startsAt: '2026-11-01T00:00:00Z',
    endsAt: '2026-11-30T00:00:00Z',
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z',
  },
  {
    id: 'vch-2',
    code: 'NHAP10',
    name: 'Bản nháp 10%',
    discountType: 'PERCENTAGE',
    discountValue: 10,
    voucherStatus: 'DRAFT',
    minimumOrderAmount: 300000,
    maxDiscountAmount: 50000,
    usageLimitTotal: 50,
    usageLimitPerCustomer: 1,
    usageHeld: 0,
    usageConsumed: 0,
    totalBudgetAmount: 1000000,
    budgetHeld: 0,
    budgetConsumed: 0,
    startsAt: '2026-11-05T00:00:00Z',
    endsAt: '2026-11-20T00:00:00Z',
    createdAt: '2026-11-04T00:00:00Z',
    updatedAt: '2026-11-04T00:00:00Z',
  },
]

let router: ReturnType<typeof createMemoryRouter> | undefined
function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

afterEach(() => {
  router?.dispose()
  localStorage.clear()
  sessionStorage.clear()
})

describe('Voucher Runtime Parsers', () => {
  test('parseVoucherPreview parses valid response and throws on invalid structure', () => {
    const valid = {
      code: 'PROMO10',
      subtotalAmount: 1000000,
      discountAmount: 100000,
      totalAmount: 900000,
      validUntil: '2026-11-30T00:00:00Z',
    }
    const parsed = parseVoucherPreview(valid)
    expect(parsed.code).toBe('PROMO10')
    expect(parsed.discountAmount).toBe(100000)
    expect(parsed.totalAmount).toBe(900000)

    expect(() => parseVoucherPreview({ code: 'BAD', discountAmount: -50 })).toThrow()
    expect(() => parseVoucherPreview('not a record')).toThrow()
  })

  test('parseAdminVoucherSummary validates amounts and status enums strictly', () => {
    const valid = sampleVouchers[0]
    const parsed = parseAdminVoucherSummary(valid)
    expect(parsed.code).toBe('GIAM50K')
    expect(parsed.voucherStatus).toBe('ACTIVE')

    expect(() =>
      parseAdminVoucherSummary({
        ...valid,
        discountType: 'UNKNOWN_TYPE',
      }),
    ).toThrow()

    expect(() =>
      parseAdminVoucherSummary({
        ...valid,
        voucherStatus: 'INVALID_STATUS',
      }),
    ).toThrow()

    expect(() =>
      parseAdminVoucherSummary({
        ...valid,
        discountValue: -100,
      }),
    ).toThrow()
  })
})

describe('Cart Voucher Customer Workflow', () => {
  test('Customer áp dụng mã giảm giá thành công: hiển thị dòng giảm giá, tính lại tổng tiền', async () => {
    await finishSignIn(customer)

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/cart`, () => HttpResponse.json(sampleCart)),
      http.get(`${root}/account/addresses`, () => HttpResponse.json([sampleAddress])),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-test-token' })),
      http.post(`${root}/vouchers/preview`, async ({ request }) => {
        const body = (await request.json()) as { code: string }
        if (body.code === 'GIAM50K') {
          return HttpResponse.json({
            code: 'GIAM50K',
            subtotalAmount: 1000000,
            discountAmount: 50000,
            totalAmount: 950000,
            validUntil: '2026-11-30T00:00:00Z',
          })
        }
        return HttpResponse.json({ code: 'VOUCHER_NOT_FOUND', message: 'Voucher không tồn tại' }, { status: 404 })
      }),
    )

    const user = userEvent.setup()
    open('/cart')

    expect(await screen.findByRole('heading', { name: 'Vợt Pro Carbon' })).toBeVisible()
    expect(screen.getByText('Tạm tính')).toBeVisible()

    // Enter voucher code
    const input = screen.getByLabelText('Mã ưu đãi')
    await user.type(input, 'giam50k')
    expect(input).toHaveValue('GIAM50K')

    await user.click(screen.getByRole('button', { name: 'Áp dụng' }))

    // Expect discount row and updated total
    expect(await screen.findByText('Mã giảm (GIAM50K)')).toBeVisible()
    expect(screen.getAllByText('-50.000 ₫').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText('Tổng thanh toán')).toBeVisible()
    expect(screen.getByText('980.000 ₫')).toBeVisible()

    // Remove voucher
    await user.click(screen.getByRole('button', { name: 'Bỏ mã' }))
    await waitFor(() => {
      expect(screen.queryByText('Mã giảm (GIAM50K)')).toBeNull()
    })
    expect(screen.queryByText('950.000 ₫')).toBeNull()
    expect(screen.getAllByText('1.000.000 ₫').length).toBeGreaterThanOrEqual(2)
  })

  test('Thay đổi số lượng sản phẩm trong giỏ tự hủy bỏ mã voucher đang áp dụng', async () => {
    await finishSignIn(customer)

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/cart`, () => HttpResponse.json(sampleCart)),
      http.get(`${root}/account/addresses`, () => HttpResponse.json([sampleAddress])),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-test-token' })),
      http.post(`${root}/vouchers/preview`, () =>
        HttpResponse.json({
          code: 'GIAM50K',
          subtotalAmount: 1000000,
          discountAmount: 50000,
          totalAmount: 950000,
          validUntil: '2026-11-30T00:00:00Z',
        }),
      ),
      http.put(`${root}/cart/items/sku-vch-1`, () =>
        HttpResponse.json({
          ...sampleCart,
          items: [{ ...sampleCart.items[0], quantity: 3, lineTotal: 1500000 }],
          totalQuantity: 3,
          subtotal: 1500000,
        }),
      ),
    )

    const user = userEvent.setup()
    open('/cart')

    await user.type(await screen.findByLabelText('Mã ưu đãi'), 'GIAM50K')
    await user.click(screen.getByRole('button', { name: 'Áp dụng' }))
    expect(await screen.findByText('Mã giảm (GIAM50K)')).toBeVisible()

    // Change quantity stepper +
    await user.click(screen.getByRole('button', { name: /Tăng số lượng/ }))

    // Expect voucher preview to be invalidated
    await waitFor(() => {
      expect(screen.queryByText('Mã giảm (GIAM50K)')).toBeNull()
    })
  })

  test('Hiển thị thông báo lỗi thân thiện khi voucher không hợp lệ', async () => {
    await finishSignIn(customer)

    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/cart`, () => HttpResponse.json(sampleCart)),
      http.get(`${root}/account/addresses`, () => HttpResponse.json([sampleAddress])),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-test-token' })),
      http.post(`${root}/vouchers/preview`, () =>
        HttpResponse.json(
          { code: 'VOUCHER_EXPIRED', message: 'Mã đã hết hạn' },
          { status: 409 },
        ),
      ),
    )

    const user = userEvent.setup()
    open('/cart')

    await user.type(await screen.findByLabelText('Mã ưu đãi'), 'EXPIRED10')
    await user.click(screen.getByRole('button', { name: 'Áp dụng' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(VOUCHER_ERROR_MESSAGES.VOUCHER_EXPIRED!)
  })
})

describe('Admin Vouchers Management Workspace', () => {
  test('Admin mở tab Mã giảm giá, xem danh sách voucher và kích hoạt bản nháp', async () => {
    await finishSignIn(admin)

    let currentVouchers = [...sampleVouchers]
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(admin)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/vouchers`, () =>
        HttpResponse.json({
          items: currentVouchers,
          page: 1,
          pageSize: 100,
          totalCount: currentVouchers.length,
        }),
      ),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-admin-token' })),
      http.post(`${root}/admin/vouchers/vch-2/activate`, () => {
        currentVouchers = currentVouchers.map(v =>
          v.id === 'vch-2' ? { ...v, voucherStatus: 'ACTIVE' } : v,
        )
        return HttpResponse.json(currentVouchers.find(v => v.id === 'vch-2'))
      }),
    )

    const user = userEvent.setup()
    open('/admin')

    // Click tab "Mã giảm giá"
    await user.click(await screen.findByRole('tab', { name: 'Mã giảm giá' }))

    // Expect header & list
    expect(await screen.findByRole('heading', { name: 'Quản lý mã giảm giá' })).toBeVisible()
    expect(await screen.findByText('GIAM50K')).toBeVisible()
    expect(await screen.findByText('Khuyến mãi mùa giải')).toBeVisible()
    expect(await screen.findByText('NHAP10')).toBeVisible()
    expect(await screen.findByText('Bản nháp 10%')).toBeVisible()
    expect(screen.getAllByText('Bản nháp').length).toBeGreaterThanOrEqual(1)

    // Click "Kích hoạt" on draft voucher NHAP10
    await user.click(screen.getByRole('button', { name: 'Kích hoạt' }))

    // Confirm dialog
    expect(await screen.findByRole('heading', { name: 'Kích hoạt mã giảm giá' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Xác nhận' }))

    // Expect status to become ACTIVE and success message
    expect(await screen.findByText(/Đã thực hiện "Kích hoạt" thành công/)).toBeVisible()
    expect((await screen.findAllByText('Đang mở')).length).toBeGreaterThanOrEqual(2)
  })

  test('Admin tạo voucher mới gửi đầy đủ tên chương trình và dữ liệu hợp lệ', async () => {
    await finishSignIn(admin)

    let createdPayload: unknown = null
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(admin)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/vouchers`, () =>
        HttpResponse.json({
          items: sampleVouchers,
          page: 1,
          pageSize: 100,
          totalCount: sampleVouchers.length,
        }),
      ),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-admin-token' })),
      http.post(`${root}/admin/vouchers`, async ({ request }) => {
        createdPayload = await request.json()
        return HttpResponse.json({
          ...sampleVouchers[0],
          id: 'vch-new',
          code: 'NEWVCH',
          name: 'Chương trình Vợt Mới 2026',
          voucherStatus: 'DRAFT',
        })
      }),
    )

    const user = userEvent.setup()
    open('/admin')

    await user.click(await screen.findByRole('tab', { name: 'Mã giảm giá' }))
    await user.click(await screen.findByRole('button', { name: '+ Tạo mã mới' }))

    await user.type(screen.getByLabelText(/Mã voucher/), 'NEWVCH')
    await user.type(screen.getByLabelText(/Tên chương trình/), 'Chương trình Vợt Mới 2026')
    await user.click(screen.getByRole('button', { name: 'Tạo bản nháp' }))

    expect(await screen.findByText('Đã tạo mã giảm giá "NEWVCH" (Bản nháp).')).toBeVisible()
    expect(createdPayload).toMatchObject({
      code: 'NEWVCH',
      name: 'Chương trình Vợt Mới 2026',
    })
  })
})
