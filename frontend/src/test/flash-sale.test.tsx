import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Admin mở tab Flash Sale, xem danh sách chiến dịch, chi tiết và hiệu suất', async () => {
  server.use(
    http.get(`${root}/auth/me`, () =>
      HttpResponse.json({ id: 'admin-1', email: 'admin@example.invalid', fullName: 'Admin FlashSale', role: 'ADMIN' })
    ),
    http.get(`${root}/auth/csrf`, () =>
      HttpResponse.json({ requestToken: 'csrf-fs-token' })
    ),
    http.get(`${root}/admin/products`, () =>
      HttpResponse.json([
        {
          id: 'prod-1',
          title: 'Bàn phím cơ HCC Pro',
          description: 'Bàn phím cơ cao cấp',
          categoryId: null,
          publicationStatus: 'PUBLISHED',
          createdAt: '2026-10-30T00:00:00Z',
          publishedAt: '2026-10-30T01:00:00Z',
          skus: [
            { id: 'sku-1', productId: 'prod-1', skuCode: 'HCC-KEY-01', optionLabel: 'Red Switch', regularPrice: 1200000, skuStatus: 'ACTIVE' },
          ],
          images: [],
        },
      ])
    ),
    http.get(`${root}/admin/flash-sales`, () =>
      HttpResponse.json([
        {
          id: 'camp-1',
          name: 'Flash Sale Giờ Vàng 11.11',
          startsAt: '2026-11-11T00:00:00Z',
          endsAt: '2026-11-11T02:00:00Z',
          campaignStatus: 'ACTIVE',
          createdBy: 'admin-1',
          createdAt: '2026-10-30T10:00:00Z',
          updatedAt: '2026-10-30T10:00:00Z',
          offersCount: 1,
          totalQuota: 50,
          totalSold: 15,
        },
      ])
    ),
    http.get(`${root}/admin/flash-sales/camp-1`, () =>
      HttpResponse.json({
        id: 'camp-1',
        name: 'Flash Sale Giờ Vàng 11.11',
        startsAt: '2026-11-11T00:00:00Z',
        endsAt: '2026-11-11T02:00:00Z',
        campaignStatus: 'ACTIVE',
        createdBy: 'admin-1',
        createdAt: '2026-10-30T10:00:00Z',
        updatedAt: '2026-10-30T10:00:00Z',
        offers: [
          {
            id: 'offer-1',
            campaignId: 'camp-1',
            productSkuId: 'sku-1',
            productId: 'prod-1',
            productTitle: 'Bàn phím cơ HCC Pro',
            skuCode: 'HCC-KEY-01',
            optionLabel: 'Red Switch',
            regularPrice: 1200000,
            salePrice: 790000,
            quotaTotal: 50,
            quotaHeld: 5,
            quotaSold: 15,
            remainingQuota: 30,
            perCustomerLimit: 2,
          },
        ],
      })
    ),
    http.get(`${root}/admin/flash-sales/camp-1/performance`, () =>
      HttpResponse.json({
        campaignId: 'camp-1',
        name: 'Flash Sale Giờ Vàng 11.11',
        status: 'ACTIVE',
        startsAt: '2026-11-11T00:00:00Z',
        endsAt: '2026-11-11T02:00:00Z',
        totalQuota: 50,
        totalHeld: 5,
        totalSold: 15,
        remainingQuota: 30,
        estimatedRevenue: 11850000,
        offers: [
          {
            offerId: 'offer-1',
            skuCode: 'HCC-KEY-01',
            productTitle: 'Bàn phím cơ HCC Pro',
            salePrice: 790000,
            quotaTotal: 50,
            quotaHeld: 5,
            quotaSold: 15,
            remainingQuota: 30,
            soldRevenue: 11850000,
          },
        ],
      })
    )
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
  render(<RouterProvider router={router} />)

  const user = userEvent.setup()
  const flashSaleTab = await screen.findByRole('tab', { name: /Flash Sale/ })
  await user.click(flashSaleTab)

  expect(await screen.findByRole('heading', { name: 'Quản trị Chiến dịch Flash Sale' })).toBeVisible()
  expect((await screen.findAllByText('Flash Sale Giờ Vàng 11.11')).length).toBeGreaterThanOrEqual(1)
  expect(screen.getByText('Bàn phím cơ HCC Pro')).toBeVisible()
  expect(screen.getByText(/790\.000/)).toBeVisible()
  expect(screen.getByText('DOANH THU ĐÃ BÁN')).toBeVisible()
})

test('Admin tạo chiến dịch DRAFT mới, thêm ưu đãi, công bố và hủy chiến dịch', async () => {
  let campaignStatus = 'DRAFT'
  let offersCount = 0

  server.use(
    http.get(`${root}/auth/me`, () =>
      HttpResponse.json({ id: 'admin-1', email: 'admin@example.invalid', fullName: 'Admin FlashSale', role: 'ADMIN' })
    ),
    http.get(`${root}/auth/csrf`, () =>
      HttpResponse.json({ requestToken: 'csrf-fs-token' })
    ),
    http.get(`${root}/admin/products`, () =>
      HttpResponse.json([
        {
          id: 'prod-1',
          title: 'Chuột Gaming HCC',
          description: '',
          categoryId: null,
          publicationStatus: 'PUBLISHED',
          createdAt: '2026-10-30T00:00:00Z',
          publishedAt: '2026-10-30T01:00:00Z',
          skus: [
            { id: 'sku-2', productId: 'prod-1', skuCode: 'HCC-MOUSE-01', optionLabel: 'Đen RGB', regularPrice: 600000, skuStatus: 'ACTIVE' },
          ],
          images: [],
        },
      ])
    ),
    http.get(`${root}/admin/flash-sales`, () =>
      HttpResponse.json(
        campaignStatus === 'NONE'
          ? []
          : [
              {
                id: 'camp-2',
                name: 'Flash Sale Black Friday',
                startsAt: '2026-11-25T00:00:00Z',
                endsAt: '2026-11-25T04:00:00Z',
                campaignStatus,
                createdBy: 'admin-1',
                createdAt: '2026-10-30T10:00:00Z',
                updatedAt: '2026-10-30T10:00:00Z',
                offersCount,
                totalQuota: offersCount * 20,
                totalSold: 0,
              },
            ]
      )
    ),
    http.get(`${root}/admin/flash-sales/camp-2`, () =>
      HttpResponse.json({
        id: 'camp-2',
        name: 'Flash Sale Black Friday',
        startsAt: '2026-11-25T00:00:00Z',
        endsAt: '2026-11-25T04:00:00Z',
        campaignStatus,
        createdBy: 'admin-1',
        createdAt: '2026-10-30T10:00:00Z',
        updatedAt: '2026-10-30T10:00:00Z',
        offers: offersCount > 0
          ? [
              {
                id: 'offer-2',
                campaignId: 'camp-2',
                productSkuId: 'sku-2',
                productId: 'prod-1',
                productTitle: 'Chuột Gaming HCC',
                skuCode: 'HCC-MOUSE-01',
                optionLabel: 'Đen RGB',
                regularPrice: 600000,
                salePrice: 390000,
                quotaTotal: 20,
                quotaHeld: 0,
                quotaSold: 0,
                remainingQuota: 20,
                perCustomerLimit: 2,
              },
            ]
          : [],
      })
    ),
    http.get(`${root}/admin/flash-sales/camp-2/performance`, () =>
      HttpResponse.json({
        campaignId: 'camp-2',
        name: 'Flash Sale Black Friday',
        status: campaignStatus,
        startsAt: '2026-11-25T00:00:00Z',
        endsAt: '2026-11-25T04:00:00Z',
        totalQuota: 20,
        totalHeld: 0,
        totalSold: 0,
        remainingQuota: 20,
        estimatedRevenue: 0,
        offers: [],
      })
    ),
    http.post(`${root}/admin/flash-sales`, async () => {
      campaignStatus = 'DRAFT'
      return HttpResponse.json({
        id: 'camp-2',
        name: 'Flash Sale Black Friday',
        startsAt: '2026-11-25T00:00:00Z',
        endsAt: '2026-11-25T04:00:00Z',
        campaignStatus: 'DRAFT',
        createdBy: 'admin-1',
        createdAt: '2026-10-30T10:00:00Z',
        updatedAt: '2026-10-30T10:00:00Z',
        offers: [],
      }, { status: 201 })
    }),
    http.post(`${root}/admin/flash-sales/camp-2/offers`, async () => {
      offersCount = 1
      return HttpResponse.json({
        id: 'offer-2',
        campaignId: 'camp-2',
        productSkuId: 'sku-2',
        productId: 'prod-1',
        productTitle: 'Chuột Gaming HCC',
        skuCode: 'HCC-MOUSE-01',
        optionLabel: 'Đen RGB',
        regularPrice: 600000,
        salePrice: 390000,
        quotaTotal: 20,
        quotaHeld: 0,
        quotaSold: 0,
        remainingQuota: 20,
        perCustomerLimit: 2,
      }, { status: 201 })
    }),
    http.post(`${root}/admin/flash-sales/camp-2/publish`, async () => {
      campaignStatus = 'SCHEDULED'
      return HttpResponse.json({
        id: 'camp-2',
        name: 'Flash Sale Black Friday',
        startsAt: '2026-11-25T00:00:00Z',
        endsAt: '2026-11-25T04:00:00Z',
        campaignStatus: 'SCHEDULED',
        createdBy: 'admin-1',
        createdAt: '2026-10-30T10:00:00Z',
        updatedAt: '2026-10-30T10:00:00Z',
        offers: [],
      })
    }),
    http.post(`${root}/admin/flash-sales/camp-2/cancel`, async () => {
      campaignStatus = 'CANCELLED'
      return HttpResponse.json({
        id: 'camp-2',
        name: 'Flash Sale Black Friday',
        startsAt: '2026-11-25T00:00:00Z',
        endsAt: '2026-11-25T04:00:00Z',
        campaignStatus: 'CANCELLED',
        createdBy: 'admin-1',
        createdAt: '2026-10-30T10:00:00Z',
        updatedAt: '2026-10-30T10:00:00Z',
        offers: [],
      })
    })
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
  render(<RouterProvider router={router} />)

  const user = userEvent.setup()
  await user.click(await screen.findByRole('tab', { name: /Flash Sale/ }))

  // Kiểm tra ban đầu có chiến dịch camp-2 ở DRAFT
  expect((await screen.findAllByText('Flash Sale Black Friday')).length).toBeGreaterThanOrEqual(1)

  // Thêm offer
  const selectSku = screen.getByRole('combobox')
  await user.selectOptions(selectSku, 'sku-2')
  await user.click(screen.getByRole('button', { name: 'Thêm' }))

  // Đã thêm offer, có nút Publish
  const publishBtn = await screen.findByRole('button', { name: /Công bố \(Publish\)/ })
  expect(publishBtn).toBeEnabled()
  await user.click(publishBtn)

  // Sau publish, chuyển sang SCHEDULED
  expect((await screen.findAllByText(/SCHEDULED/)).length).toBeGreaterThanOrEqual(1)

  // Bấm hủy chiến dịch
  const cancelBtn = screen.getByRole('button', { name: 'Hủy chiến dịch' })
  await user.click(cancelBtn)

  // Modal xác nhận xuất hiện kèm cảnh báo SM-Q02
  const dialog = await screen.findByRole('dialog')
  expect(dialog).toBeVisible()
  expect(within(dialog).getByText(/SM-Q02/)).toBeVisible()

  // Xác nhận hủy
  await user.click(screen.getByRole('button', { name: 'Xác nhận Hủy' }))

  // Trạng thái CANCELLED
  expect((await screen.findAllByText(/CANCELLED/)).length).toBeGreaterThanOrEqual(1)
})

test('Customer đi từ Flash Sale đến sản phẩm, giỏ và checkout giữ nguyên offer id cùng giá sale', async () => {
  const customer = { id: 'customer-fs', email: 'customer-fs@example.invalid', fullName: 'Khách Flash Sale', role: 'CUSTOMER' }
  const offer = {
    offerId: 'offer-public-1', productSkuId: 'sku-racket-fs', productId: 'product-racket-fs',
    productTitle: 'Vợt Astrox Flash', skuCode: 'ASTROX-FS', optionLabel: '4U G5',
    regularPrice: 3000000, salePrice: 2100000, discountPercentage: 30,
    quotaTotal: 10, remainingQuota: 7, perCustomerLimit: 2, imageUrl: null,
  }
  const campaign = {
    id: 'campaign-public-1', name: 'Giờ vàng sân cầu', startsAt: '2026-10-30T00:00:00Z',
    endsAt: '2027-10-30T23:59:59Z', campaignStatus: 'ACTIVE', offers: [offer],
  }
  const cart = {
    id: 'cart-fs', items: [{
      productSkuId: offer.productSkuId, productId: offer.productId, productTitle: offer.productTitle,
      skuCode: offer.skuCode, optionLabel: offer.optionLabel, unitPrice: offer.salePrice,
      quantity: 1, lineTotal: offer.salePrice, imageUrl: null, availabilityCode: 'AVAILABLE',
      flashSaleOfferId: offer.offerId,
    }], totalQuantity: 1, subtotal: offer.salePrice, canCheckout: true, updatedAt: '2026-10-30T00:00:00Z',
  }
  const address = {
    id: 'address-fs', recipientName: 'Người nhận Flash', phone: '0901234567', addressLine: '1 Đường Cầu Lông',
    ward: 'Phường Sân', province: 'TP Hồ Chí Minh', isDefault: true, createdAt: '2026-10-30T00:00:00Z',
  }
  let checkoutBody: unknown
  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
    http.get(`${root}/catalog/categories`, () => HttpResponse.json([])),
    http.get(`${root}/catalog/products`, () => HttpResponse.json({ items: [], page: 1, pageSize: 12, totalCount: 0 })),
    http.get(`${root}/catalog/products/${offer.productId}`, () => HttpResponse.json({
      id: offer.productId, title: offer.productTitle, description: 'Vợt thi đấu', category: null,
      skus: [{ id: offer.productSkuId, skuCode: offer.skuCode, optionLabel: offer.optionLabel, price: offer.regularPrice, inStock: true }],
      images: [],
    })),
    http.get(`${root}/catalog/flash-sales`, () => HttpResponse.json([campaign])),
    http.post(`${root}/cart/merge`, async ({ request }) => {
      expect(await request.json()).toEqual({ items: [{ productSkuId: offer.productSkuId, quantity: 1, flashSaleOfferId: offer.offerId }] })
      return HttpResponse.json({ cart, issues: [] })
    }),
    http.get(`${root}/cart`, () => HttpResponse.json(cart)),
    http.get(`${root}/account/addresses`, () => HttpResponse.json([address])),
    http.post(`${root}/checkouts`, async ({ request }) => {
      checkoutBody = await request.json()
      return HttpResponse.json({
        orderId: 'order-fs', orderNumber: 'HC-FLASH-001', orderStatus: 'PENDING_COD_CONFIRMATION',
        paymentMethod: 'COD', paymentStatus: 'COD_PENDING', totalAmount: offer.salePrice,
        deadlineAt: '2026-10-31T00:00:00Z', shippingAddress: address,
        items: [{ productSkuId: offer.productSkuId, productTitle: offer.productTitle, skuCode: offer.skuCode,
          optionLabel: offer.optionLabel, quantity: 1, unitPrice: offer.salePrice, lineTotal: offer.salePrice }],
      }, { status: 201 })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('link', { name: 'Săn Deal Ngay' }))
  expect(await screen.findByText('FLASH SALE')).toBeVisible()
  expect(screen.getByText(/2\.100\.000/)).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Thêm vào giỏ' }))
  await user.click(await screen.findByRole('link', { name: 'Xem giỏ' }))
  await user.click(await screen.findByRole('button', { name: 'Đặt hàng và giữ tồn' }))
  expect(await screen.findByRole('heading', { name: 'HC-FLASH-001' })).toBeVisible()
  expect(checkoutBody).toEqual({
    shippingAddressId: address.id, paymentMethod: 'COD',
    lines: [{ productSkuId: offer.productSkuId, quantity: 1, flashSaleOfferId: offer.offerId }],
  })
})
