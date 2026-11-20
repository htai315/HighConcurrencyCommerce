import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'

const customer = {
  id: 'customer-returns',
  email: 'cust_ret@example.invalid',
  fullName: 'Khách hàng Trả hàng',
  role: 'CUSTOMER',
}

const staff = {
  id: 'staff-returns',
  email: 'staff_ret@example.invalid',
  fullName: 'Nhân viên Kho',
  role: 'STAFF',
}

const admin = {
  id: 'admin-returns',
  email: 'admin_ret@example.invalid',
  fullName: 'Quản trị viên',
  role: 'ADMIN',
}

const sampleOrder = {
  id: 'order-ret-001',
  orderNumber: 'ORD-RET-001',
  orderStatus: 'COMPLETED',
  paymentMethod: 'COD',
  paymentStatus: 'COD_COLLECTED',
  reservationStatus: 'CONSUMED',
  totalAmount: 430000,
  subtotalAmount: 400000,
  voucherDiscountAmount: 0,
  voucherCode: null,
  shippingFee: 30000,
  shippingMethodCode: 'STANDARD',
  shippingMethodName: 'Giao hàng tiêu chuẩn',
  shippingEstimatedMinDays: 2,
  shippingEstimatedMaxDays: 4,
  createdAt: '2026-11-04T10:00:00Z',
  deadlineAt: '2026-11-05T10:00:00Z',
  confirmedAt: '2026-11-04T10:30:00Z',
  closedReason: null,
  recipientName: 'Khách hàng',
  recipientPhone: '0901234567',
  shippingAddress: '123 Phố Trả Hàng, Hà Nội',
  shipmentId: 'shipment-ret-001',
  shipmentStatus: 'DELIVERED',
  shippedAt: '2026-11-04T11:00:00Z',
  deliveredAt: '2026-11-04T15:00:00Z',
  redeliveryCount: 0,
  canCancel: false,
  cancellationReason: null,
  items: [
    {
      productId: 'prod-ret-1',
      productSkuId: 'sku-ret-1',
      productTitle: 'Áo cầu lông thể thao',
      skuCode: 'RET-TSHIRT-L',
      optionLabel: 'Size L',
      quantity: 2,
      unitPrice: 200000,
      lineTotal: 400000,
    },
  ],
  deliveryAttempts: [],
}

const sampleEligibility = {
  isEligible: true,
  ineligibleReason: null,
  deliveredAt: '2026-11-04T15:00:00Z',
  windowExpiresAt: '2026-11-11T15:00:00Z',
  shippingFee: 30000,
  orderTotalAmount: 430000,
  items: [
    {
      orderItemId: 'item-ret-001',
      productSkuId: 'sku-ret-1',
      productTitle: 'Áo cầu lông thể thao',
      skuCode: 'RET-TSHIRT-L',
      optionLabel: 'Size L',
      orderedQuantity: 2,
      unitPrice: 200000,
      lineTotal: 400000,
      maxRefundableAmount: 400000,
    },
  ],
}

const sampleReturnSummary = {
  id: 'ret-req-001',
  orderId: 'order-ret-001',
  orderNumber: 'ORD-RET-001',
  returnStatus: 'REQUESTED',
  reasonCode: 'DEFECTIVE',
  refundAmount: 200000,
  totalItemCount: 1,
  requestedAt: '2026-11-05T08:00:00Z',
  updatedAt: '2026-11-05T08:00:00Z',
  evidenceReference: null,
}

const sampleAdminReturnSummary = {
  id: 'ret-req-001',
  orderId: 'order-ret-001',
  orderNumber: 'ORD-RET-001',
  customerId: 'customer-returns',
  customerEmail: 'cust_ret@example.invalid',
  customerFullName: 'Khách hàng Trả hàng',
  returnStatus: 'REQUESTED',
  reasonCode: 'DEFECTIVE',
  refundAmount: 200000,
  totalItemCount: 1,
  requestedAt: '2026-11-05T08:00:00Z',
  decidedAt: null,
  receivedAt: null,
  refundedAt: null,
  updatedAt: '2026-11-05T08:00:00Z',
}

const sampleStaffReturnSummary = {
  id: 'ret-req-001',
  orderId: 'order-ret-001',
  orderNumber: 'ORD-RET-001',
  returnStatus: 'APPROVED',
  reasonCode: 'DEFECTIVE',
  refundAmount: 200000,
  totalItemCount: 1,
  requestedAt: '2026-11-05T08:00:00Z',
  decidedAt: '2026-11-05T08:30:00Z',
  decisionNote: 'Đồng ý nhận lại hàng',
}

const sampleReceivedReturnDetail = {
  id: 'ret-req-001', orderId: 'order-ret-001', orderNumber: 'ORD-RET-001',
  customerId: 'customer-returns', customerEmail: 'cust_ret@example.invalid', customerFullName: 'Khách hàng Trả hàng',
  returnStatus: 'RECEIVED', reasonCode: 'DEFECTIVE', customerNote: null, refundAmount: 200000,
  requestedAt: '2026-11-05T08:00:00Z', decidedBy: 'admin-id', decidedByEmail: 'admin@example.invalid',
  decidedAt: '2026-11-05T08:30:00Z', decisionNote: 'Đồng ý', receivedBy: 'staff-returns',
  receivedByEmail: 'staff_ret@example.invalid', receivedAt: '2026-11-05T09:00:00Z', receivedEvidence: 'BB-001',
  inspectedBy: null, inspectedByEmail: null, inspectedAt: null, inspectionNote: null,
  refundedAt: null, evidenceReference: null,
  lines: [{
    id: 'line-001', orderItemId: 'item-ret-001', productSkuId: 'sku-ret-1', productTitle: 'Áo cầu lông thể thao',
    skuCode: 'RET-TSHIRT-L', optionLabel: 'Size L', requestedQty: 1, unitPrice: 200000, grossAmount: 200000,
    allocatedVoucherDiscount: 0, refundAmount: 200000, restockQty: 0, quarantineQty: 0, damagedQty: 0, scrappedQty: 0,
  }],
  revision: 2, createdAt: '2026-11-05T08:00:00Z', updatedAt: '2026-11-05T09:00:00Z',
}

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

describe('T39: Returns and Refunds Frontend', () => {
  test('Customer selects direct exchange target and submits resolution contract', async () => {
    let submitted: Record<string, unknown> | null = null
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/orders`, () => HttpResponse.json({ items: [sampleOrder], page: 1, pageSize: 50, totalCount: 1 })),
      http.get(`${root}/orders/order-ret-001/return-eligibility`, () => HttpResponse.json({
        ...sampleEligibility,
        items: sampleEligibility.items.map(item => ({ ...item, exchangeTargets: [{ skuId: 'sku-ret-xl', skuCode: 'RET-TSHIRT-XL', optionLabel: 'Size XL', regularPrice: 200000, availableQty: 3 }] })),
      })),
      http.post(`${root}/orders/order-ret-001/returns`, async ({ request }) => {
        submitted = await request.json() as Record<string, unknown>
        return HttpResponse.json({ ...sampleReceivedReturnDetail, resolutionType: 'EXCHANGE', returnStatus: 'REQUESTED', receivedAt: null, exchangeShipment: null }, { status: 201 })
      }),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/orders'] })
    render(<RouterProvider router={router} />)
    await userEvent.click(await screen.findByRole('button', { name: /Yêu cầu đổi \/ trả hàng/i }))
    const modal = await screen.findByRole('dialog', { name: /Yêu cầu đổi\/trả hàng/i })
    await userEvent.click(within(modal).getByLabelText(/Đổi phân loại/i))
    await userEvent.click(within(modal).getByRole('button', { name: /Gửi yêu cầu đổi\/trả/i }))
    await waitFor(() => expect(submitted).not.toBeNull())
    expect(submitted).toMatchObject({ resolutionType: 'EXCHANGE', items: [{ targetSkuId: 'sku-ret-xl' }] })
  })

  test('Customer orders page displays return button and opens CreateReturnModal', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/orders`, () => HttpResponse.json({ items: [sampleOrder], page: 1, pageSize: 50, totalCount: 1 })),
      http.get(`${root}/orders/order-ret-001/return-eligibility`, () => HttpResponse.json(sampleEligibility)),
      http.post(`${root}/orders/order-ret-001/returns`, () =>
        HttpResponse.json(
          {
            id: 'ret-req-001',
            orderId: 'order-ret-001',
            orderNumber: 'ORD-RET-001',
            returnStatus: 'REQUESTED',
            reasonCode: 'DEFECTIVE',
            customerNote: 'Lỗi chỉ may',
            refundAmount: 200000,
            requestedAt: '2026-11-05T08:00:00Z',
            decidedAt: null,
            decisionNote: null,
            receivedAt: null,
            refundedAt: null,
            lines: [
              {
                id: 'line-001',
                orderItemId: 'item-ret-001',
                productSkuId: 'sku-ret-1',
                productTitle: 'Áo cầu lông thể thao',
                skuCode: 'RET-TSHIRT-L',
                optionLabel: 'Size L',
                requestedQty: 1,
                unitPrice: 200000,
                grossAmount: 200000,
                allocatedVoucherDiscount: 0,
                refundAmount: 200000,
              },
            ],
            createdAt: '2026-11-05T08:00:00Z',
            updatedAt: '2026-11-05T08:00:00Z',
          },
          { status: 201 }
        )
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/orders'] })
    render(<RouterProvider router={router} />)

    // Verify order card rendered
    expect(await screen.findByText('ORD-RET-001')).toBeInTheDocument()

    // Find and click return button
    const returnBtn = await screen.findByRole('button', { name: /Yêu cầu đổi \/ trả hàng/i })
    expect(returnBtn).toBeInTheDocument()
    await userEvent.click(returnBtn)

    // Modal opens
    const modal = await screen.findByRole('dialog', { name: /Yêu cầu đổi\/trả hàng/i })
    expect(modal).toBeInTheDocument()
    expect(within(modal).getByText('Áo cầu lông thể thao')).toBeInTheDocument()
    expect(within(modal).getByText(/Phí vận chuyển 30\.000.*không nằm trong số tiền hoàn/)).toBeInTheDocument()

    // Submit return request
    const submitBtn = screen.getByRole('button', { name: /Gửi yêu cầu đổi\/trả/i })
    await userEvent.click(submitBtn)

    // Success alert displayed
    await waitFor(() => {
      expect(screen.getByText(/Yêu cầu đổi\/trả đã được gửi thành công/i)).toBeInTheDocument()
    })
  })

  test('Customer returns page renders list and allows cancellation', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/account/returns`, () =>
        HttpResponse.json({ items: [sampleReturnSummary], page: 1, pageSize: 20, totalCount: 1 })
      ),
      http.post(`${root}/account/returns/ret-req-001/cancel`, () =>
        HttpResponse.json({
          id: 'ret-req-001',
          orderId: 'order-ret-001',
          orderNumber: 'ORD-RET-001',
          returnStatus: 'CANCELLED',
          reasonCode: 'DEFECTIVE',
          customerNote: null,
          refundAmount: 200000,
          requestedAt: '2026-11-05T08:00:00Z',
          decidedAt: null,
          decisionNote: null,
          receivedAt: null,
          refundedAt: null,
          lines: [],
          createdAt: '2026-11-05T08:00:00Z',
          updatedAt: '2026-11-05T08:15:00Z',
        })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/returns'] })
    render(<RouterProvider router={router} />)

    // Verify list item rendered
    expect(await screen.findByText(/Đơn hàng #ORD-RET-001/i)).toBeInTheDocument()
    expect(screen.getByText('Chờ duyệt')).toBeInTheDocument()

    // Click cancel button
    const cancelBtn = screen.getByRole('button', { name: /Hủy yêu cầu trả hàng/i })
    await userEvent.click(cancelBtn)

    // Confirm dialog
    const confirmBtn = await screen.findByRole('button', { name: /Xác nhận hủy/i })
    await userEvent.click(confirmBtn)

    // Cancelled message appears
    await waitFor(() => {
      expect(screen.getByText(/Đã hủy yêu cầu trả hàng cho đơn ORD-RET-001/i)).toBeInTheDocument()
    })
  })

  test('Customer exchange history renders resolution and replacement timeline', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(customer)),
      http.get(`${root}/account/returns`, () => HttpResponse.json({
        items: [{ ...sampleReturnSummary, resolutionType: 'EXCHANGE', returnStatus: 'EXCHANGE_SHIPPING' }],
        page: 1, pageSize: 20, totalCount: 1,
      })),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/account/returns'] })
    render(<RouterProvider router={router} />)
    expect(await screen.findByText('Đổi sang SKU khác')).toBeInTheDocument()
    expect(screen.getByText('Không thu/hoàn chênh lệch')).toBeInTheDocument()
    expect(screen.getByLabelText('Tiến trình đổi sản phẩm')).toBeInTheDocument()
    expect(screen.getAllByText('Đang giao hàng thay thế').length).toBeGreaterThan(0)
  })

  test('Staff can receive an approved return into quarantine', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/staff/products`, () => HttpResponse.json([])),
      http.get(`${root}/staff/returns/approved`, () =>
        HttpResponse.json({ items: [sampleStaffReturnSummary], page: 1, pageSize: 20, totalCount: 1 })
      ),
      http.post(`${root}/staff/returns/ret-req-001/receive`, () =>
        HttpResponse.json({
          id: 'ret-req-001',
          orderId: 'order-ret-001',
          orderNumber: 'ORD-RET-001',
          customerId: 'customer-returns',
          customerEmail: 'cust_ret@example.invalid',
          customerFullName: 'Khách hàng Trả hàng',
          returnStatus: 'RECEIVED',
          reasonCode: 'DEFECTIVE',
          customerNote: null,
          refundAmount: 200000,
          requestedAt: '2026-11-05T08:00:00Z',
          decidedBy: 'admin-id',
          decidedByEmail: 'admin@example.invalid',
          decidedAt: '2026-11-05T08:30:00Z',
          decisionNote: 'Đồng ý',
          receivedBy: 'staff-returns',
          receivedByEmail: 'staff_ret@example.invalid',
          receivedAt: '2026-11-05T09:00:00Z',
          receivedEvidence: 'Đã nhận đủ hàng kiểm tra',
          refundedAt: null,
          evidenceReference: null,
          lines: [],
          revision: 2,
          createdAt: '2026-11-05T08:00:00Z',
          updatedAt: '2026-11-05T09:00:00Z',
        })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
    render(<RouterProvider router={router} />)

    // Switch to 'Nhận hàng trả' tab
    const tabBtn = await screen.findByRole('button', { name: /Nhận hàng trả/i })
    await userEvent.click(tabBtn)

    // Queue item displayed
    expect(await screen.findByText(/Đơn hàng #ORD-RET-001/i)).toBeInTheDocument()

    const receiveBtn = screen.getByRole('button', { name: /Nhận vào khu cách ly/i })
    await userEvent.click(receiveBtn)

    // Dialog appears
    const evidenceInput = await screen.findByLabelText(/Biên bản \/ Ghi chú kiểm tra hàng/i)
    await userEvent.type(evidenceInput, 'Biên bản nhận hàng BB-001')

    const confirmReceiveBtn = screen.getByRole('button', { name: /Xác nhận nhận vào cách ly/i })
    await userEvent.click(confirmReceiveBtn)

    // Success notification
    await waitFor(() => {
      expect(screen.getByText(/Đã nhận hàng đơn ORD-RET-001 vào khu cách ly/i)).toBeInTheDocument()
    })
  })

  test('Staff classifies every received unit before completing inspection', async () => {
    let submitted: unknown = null
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/staff/products`, () => HttpResponse.json([])),
      http.get(`${root}/staff/returns/approved`, () => HttpResponse.json({
        items: [{ ...sampleStaffReturnSummary, returnStatus: 'RECEIVED' }], page: 1, pageSize: 20, totalCount: 1,
      })),
      http.get(`${root}/staff/returns/ret-req-001`, () => HttpResponse.json(sampleReceivedReturnDetail)),
      http.post(`${root}/staff/returns/ret-req-001/inspect`, async ({ request }) => {
        submitted = await request.json()
        return HttpResponse.json({
          ...sampleReceivedReturnDetail,
          returnStatus: 'INSPECTED',
          inspectedBy: 'staff-returns', inspectedByEmail: 'staff_ret@example.invalid', inspectedAt: '2026-11-05T09:10:00Z',
          lines: [{ ...sampleReceivedReturnDetail.lines[0], restockQty: 1 }], revision: 3,
        })
      }),
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
    render(<RouterProvider router={router} />)
    await userEvent.click(await screen.findByRole('button', { name: /Nhận hàng trả/i }))
    await userEvent.click(await screen.findByRole('button', { name: /Kiểm định chất lượng/i }))
    expect(await screen.findByRole('dialog', { name: 'Kiểm định hàng trả' })).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Ghi chú kiểm định'), 'Đủ điều kiện bán lại')
    await userEvent.click(screen.getByRole('button', { name: 'Hoàn tất kiểm định' }))

    await waitFor(() => expect(screen.getByText(/Đã kiểm định hàng trả của đơn ORD-RET-001/i)).toBeInTheDocument())
    expect(submitted).toMatchObject({
      inspectionNote: 'Đủ điều kiện bán lại',
      lines: [{ returnLineId: 'line-001', restockQty: 1, quarantineQty: 0, damagedQty: 0, scrappedQty: 0 }],
    })
  })

  test('Admin can approve return and process manual refund', async () => {
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(admin)),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/returns`, () =>
        HttpResponse.json({ items: [sampleAdminReturnSummary], page: 1, pageSize: 20, totalCount: 1 })
      ),
      http.post(`${root}/admin/returns/ret-req-001/approve`, () =>
        HttpResponse.json({
          id: 'ret-req-001',
          orderId: 'order-ret-001',
          orderNumber: 'ORD-RET-001',
          customerId: 'customer-returns',
          customerEmail: 'cust_ret@example.invalid',
          customerFullName: 'Khách hàng Trả hàng',
          returnStatus: 'APPROVED',
          reasonCode: 'DEFECTIVE',
          customerNote: null,
          refundAmount: 200000,
          requestedAt: '2026-11-05T08:00:00Z',
          decidedBy: 'admin-returns',
          decidedByEmail: 'admin@example.invalid',
          decidedAt: '2026-11-05T08:30:00Z',
          decisionNote: 'Duyệt',
          receivedBy: null,
          receivedByEmail: null,
          receivedAt: null,
          receivedEvidence: null,
          refundedAt: null,
          evidenceReference: null,
          lines: [],
          revision: 1,
          createdAt: '2026-11-05T08:00:00Z',
          updatedAt: '2026-11-05T08:30:00Z',
        })
      )
    )

    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)

    // Switch to 'Đổi trả & Hoàn tiền' tab
    const tabBtn = await screen.findByRole('tab', { name: /Đổi trả & Hoàn tiền/i })
    await userEvent.click(tabBtn)

    // Table row rendered
    expect(await screen.findByText('ORD-RET-001')).toBeInTheDocument()

    // Click 'Duyệt'
    const approveBtn = screen.getByRole('button', { name: /^Duyệt$/i })
    await userEvent.click(approveBtn)

    // Confirm approve
    const confirmApproveBtn = await screen.findByRole('button', { name: /Xác nhận duyệt/i })
    await userEvent.click(confirmApproveBtn)

    // Success notification
    await waitFor(() => {
      expect(screen.getByText(/Đã phê duyệt yêu cầu cho đơn ORD-RET-001/i)).toBeInTheDocument()
    })
  })

  test('Admin sees source to target SKU and reserve warning for exchange approval', async () => {
    const exchangeSummary = { ...sampleAdminReturnSummary, resolutionType: 'EXCHANGE' }
    const detail = {
      ...sampleReceivedReturnDetail, resolutionType: 'EXCHANGE', returnStatus: 'REQUESTED', receivedAt: null,
      lines: [{ ...sampleReceivedReturnDetail.lines[0], exchange: {
        id: 'exchange-line-1', targetSkuId: 'sku-xl', targetSkuCode: 'RET-TSHIRT-XL', targetOptionLabel: 'Size XL',
        targetRegularPrice: 200000, quantity: 1, allocationStatus: null, allocationExpiresAt: null,
      } }], exchangeShipment: null,
    }
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(admin)),
      http.get(`${root}/admin/products`, () => HttpResponse.json([])),
      http.get(`${root}/admin/returns`, () => HttpResponse.json({ items: [exchangeSummary], page: 1, pageSize: 20, totalCount: 1 })),
      http.get(`${root}/admin/returns/ret-req-001`, () => HttpResponse.json(detail)),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/admin'] })
    render(<RouterProvider router={router} />)
    await userEvent.click(await screen.findByRole('tab', { name: /Đổi trả & Hoàn tiền/i }))
    expect(await screen.findByText('Đổi SKU')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Chi tiết' }))
    expect(await screen.findByText(/Size XL · RET-TSHIRT-XL/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Đóng' }))
    await userEvent.click(screen.getByRole('button', { name: /^Duyệt$/ }))
    expect(await screen.findByText(/giữ tồn SKU thay thế trong 7 ngày/i)).toBeInTheDocument()
  })

  test('Staff replacement shipment panel advances packing and handover states', async () => {
    let shipmentStatus = 'PENDING_PACKING'
    const payload = () => ({
      returnRequestId: 'ret-ex-1', shipmentId: 'ship-ex-1', orderId: 'order-ret-001', orderNumber: 'ORD-RET-001',
      returnStatus: shipmentStatus === 'PENDING_PACKING' ? 'EXCHANGE_READY' : shipmentStatus === 'PACKED' ? 'EXCHANGE_PACKED' : 'EXCHANGE_SHIPPING',
      shipmentStatus, redeliveryCount: 0, shippedAt: null, deliveredAt: null,
      lines: [{ targetSkuId: 'sku-xl', skuCode: 'RET-TSHIRT-XL', optionLabel: 'Size XL', quantity: 1 }],
    })
    server.use(
      http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
      http.get(`${root}/staff/products`, () => HttpResponse.json([])),
      http.get(`${root}/staff/returns/approved`, () => HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })),
      http.get(`${root}/staff/exchange-shipments`, () => HttpResponse.json([payload()])),
      http.get(`${root}/auth/csrf`, () => HttpResponse.json({ requestToken: 'csrf-token' })),
      http.post(`${root}/staff/exchange-shipments/ship-ex-1/pack`, () => { shipmentStatus = 'PACKED'; return HttpResponse.json(payload()) }),
      http.post(`${root}/staff/exchange-shipments/ship-ex-1/handover`, () => { shipmentStatus = 'SHIPPING'; return HttpResponse.json(payload()) }),
    )
    router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
    render(<RouterProvider router={router} />)
    await userEvent.click(await screen.findByRole('button', { name: /Nhận hàng trả/i }))
    await userEvent.click(await screen.findByRole('button', { name: 'Đóng gói' }))
    expect(await screen.findByText('PACKED')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Bàn giao vận chuyển' }))
    expect(await screen.findByText('SHIPPING')).toBeInTheDocument()
  })
})
