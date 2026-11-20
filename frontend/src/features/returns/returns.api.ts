import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { requireRole } from '../auth/auth.api'

export type OrderReturnReason = 'WRONG_ITEM' | 'WRONG_SIZE' | 'DEFECTIVE' | 'NOT_AS_DESCRIBED' | 'OTHER'

export type OrderReturnStatus = 'REQUESTED' | 'APPROVED' | 'REJECTED' | 'CANCELLED' | 'RECEIVED' | 'INSPECTED' | 'REFUNDED'
  | 'EXCHANGE_READY' | 'EXCHANGE_PACKED' | 'EXCHANGE_SHIPPING' | 'EXCHANGED' | 'EXCHANGE_EXPIRED' | 'EXCHANGE_DELIVERY_FAILED'

export const returnReasonLabels: Record<string, string> = {
  WRONG_ITEM: 'Giao sai sản phẩm',
  WRONG_SIZE: 'Sai kích thước / phân loại',
  DEFECTIVE: 'Sản phẩm lỗi / hư hỏng',
  NOT_AS_DESCRIBED: 'Không đúng mô tả',
  OTHER: 'Lý do khác',
}

export const returnStatusLabels: Record<string, string> = {
  REQUESTED: 'Chờ duyệt',
  APPROVED: 'Đã duyệt (Chờ gửi/nhận)',
  REJECTED: 'Từ chối',
  CANCELLED: 'Đã hủy',
  RECEIVED: 'Đã nhận hàng (Chờ kiểm định)',
  INSPECTED: 'Đã kiểm định (Chờ hoàn tiền)',
  REFUNDED: 'Đã hoàn tiền',
  EXCHANGE_READY: 'Chờ đóng gói hàng thay thế',
  EXCHANGE_PACKED: 'Đã đóng gói hàng thay thế',
  EXCHANGE_SHIPPING: 'Đang giao hàng thay thế',
  EXCHANGED: 'Đổi hàng thành công',
  EXCHANGE_EXPIRED: 'Hết hạn giữ hàng thay thế',
  EXCHANGE_DELIVERY_FAILED: 'Giao hàng thay thế thất bại',
}

export type ExchangeTargetSku = { skuId: string; skuCode: string; optionLabel: string; regularPrice: number; availableQty: number }

export type EligibleOrderItem = {
  orderItemId: string
  productSkuId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  orderedQuantity: number
  unitPrice: number
  lineTotal: number
  maxRefundableAmount: number
  exchangeTargets: ExchangeTargetSku[]
}

export type OrderReturnEligibility = {
  isEligible: boolean
  ineligibleReason: string | null
  deliveredAt: string | null
  windowExpiresAt: string | null
  items: EligibleOrderItem[]
  shippingFee: number
  orderTotalAmount: number
}

export type CreateOrderReturnRequest = {
  resolutionType: 'RETURN' | 'EXCHANGE'
  reasonCode: string
  customerNote?: string | null
  items: { orderItemId: string; requestedQuantity: number; targetSkuId?: string | null }[]
}

export type OrderReturnLine = {
  id: string
  orderItemId: string
  productSkuId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  requestedQty: number
  unitPrice: number
  grossAmount: number
  allocatedVoucherDiscount: number
  refundAmount: number
  restockQty: number
  quarantineQty: number
  damagedQty: number
  scrappedQty: number
  exchange: null | { id: string; targetSkuId: string; targetSkuCode: string; targetOptionLabel: string; targetRegularPrice: number; quantity: number; allocationStatus: string | null; allocationExpiresAt: string | null }
}

export type CustomerOrderReturnSummary = {
  id: string
  orderId: string
  orderNumber: string
  resolutionType: string
  returnStatus: string
  reasonCode: string
  refundAmount: number
  totalItemCount: number
  requestedAt: string
  updatedAt: string
  evidenceReference: string | null
}

export type CustomerOrderReturnPage = {
  items: CustomerOrderReturnSummary[]
  page: number
  pageSize: number
  totalCount: number
}

export type CustomerOrderReturnDetail = {
  id: string
  orderId: string
  orderNumber: string
  resolutionType: string
  returnStatus: string
  reasonCode: string
  customerNote: string | null
  refundAmount: number
  shippingFee: number
  orderTotalAmount: number
  requestedAt: string
  decidedAt: string | null
  decisionNote: string | null
  receivedAt: string | null
  refundedAt: string | null
  evidenceReference: string | null
  lines: OrderReturnLine[]
  exchangeShipment: null | { id: string; shipmentStatus: string; redeliveryCount: number; shippedAt: string | null; deliveredAt: string | null }
  createdAt: string
  updatedAt: string
}

export type AdminOrderReturnSummary = {
  id: string
  orderId: string
  orderNumber: string
  customerId: string
  customerEmail: string
  customerFullName: string
  resolutionType: string
  returnStatus: string
  reasonCode: string
  refundAmount: number
  totalItemCount: number
  requestedAt: string
  decidedAt: string | null
  receivedAt: string | null
  refundedAt: string | null
  updatedAt: string
}

export type AdminOrderReturnPage = {
  items: AdminOrderReturnSummary[]
  page: number
  pageSize: number
  totalCount: number
}

export type AdminOrderReturnDetail = {
  id: string
  orderId: string
  orderNumber: string
  customerId: string
  customerEmail: string
  customerFullName: string
  resolutionType: string
  returnStatus: string
  reasonCode: string
  customerNote: string | null
  refundAmount: number
  requestedAt: string
  decidedBy: string | null
  decidedByEmail: string | null
  decidedAt: string | null
  decisionNote: string | null
  receivedBy: string | null
  receivedByEmail: string | null
  receivedAt: string | null
  receivedEvidence: string | null
  inspectedBy: string | null
  inspectedByEmail: string | null
  inspectedAt: string | null
  inspectionNote: string | null
  refundedAt: string | null
  evidenceReference: string | null
  lines: OrderReturnLine[]
  exchangeShipment: CustomerOrderReturnDetail['exchangeShipment']
  revision: number
  createdAt: string
  updatedAt: string
}

export type StaffOrderReturnSummary = {
  id: string
  orderId: string
  orderNumber: string
  resolutionType: string
  returnStatus: string
  reasonCode: string
  refundAmount: number
  totalItemCount: number
  requestedAt: string
  decidedAt: string | null
  decisionNote: string | null
}

export type StaffOrderReturnPage = {
  items: StaffOrderReturnSummary[]
  page: number
  pageSize: number
  totalCount: number
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function nullableText(value: unknown): string | null { return value === null || value === undefined ? null : text(value) }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) ? value : invalid() }
function integer(value: unknown, min = 0): number {
  const result = number(value)
  return Number.isSafeInteger(result) && result >= min ? result : invalid()
}

function parseReturnLine(value: unknown): OrderReturnLine {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id),
    orderItemId: text(value.orderItemId),
    productSkuId: text(value.productSkuId),
    productTitle: text(value.productTitle),
    skuCode: text(value.skuCode),
    optionLabel: text(value.optionLabel),
    requestedQty: integer(value.requestedQty, 1),
    unitPrice: number(value.unitPrice),
    grossAmount: number(value.grossAmount),
    allocatedVoucherDiscount: number(value.allocatedVoucherDiscount),
    refundAmount: number(value.refundAmount),
    restockQty: value.restockQty === undefined ? 0 : integer(value.restockQty),
    quarantineQty: value.quarantineQty === undefined ? 0 : integer(value.quarantineQty),
    damagedQty: value.damagedQty === undefined ? 0 : integer(value.damagedQty),
    scrappedQty: value.scrappedQty === undefined ? 0 : integer(value.scrappedQty),
    exchange: value.exchange == null ? null : (() => {
      if (!isRecord(value.exchange)) return invalid()
      return { id: text(value.exchange.id), targetSkuId: text(value.exchange.targetSkuId), targetSkuCode: text(value.exchange.targetSkuCode), targetOptionLabel: text(value.exchange.targetOptionLabel), targetRegularPrice: number(value.exchange.targetRegularPrice), quantity: integer(value.exchange.quantity, 1), allocationStatus: nullableText(value.exchange.allocationStatus), allocationExpiresAt: nullableText(value.exchange.allocationExpiresAt) }
    })(),
  }
}

export function parseReturnEligibility(value: unknown): OrderReturnEligibility {
  if (!isRecord(value) || typeof value.isEligible !== 'boolean' || !Array.isArray(value.items)) return invalid()
  return {
    isEligible: value.isEligible,
    ineligibleReason: nullableText(value.ineligibleReason),
    deliveredAt: nullableText(value.deliveredAt),
    windowExpiresAt: nullableText(value.windowExpiresAt),
    shippingFee: typeof value.shippingFee === 'number' ? number(value.shippingFee) : 0,
    orderTotalAmount: typeof value.orderTotalAmount === 'number' ? number(value.orderTotalAmount) : 0,
    items: value.items.map(item => {
      if (!isRecord(item)) return invalid()
      return {
        orderItemId: text(item.orderItemId),
        productSkuId: text(item.productSkuId),
        productTitle: text(item.productTitle),
        skuCode: text(item.skuCode),
        optionLabel: text(item.optionLabel),
        orderedQuantity: integer(item.orderedQuantity, 1),
        unitPrice: number(item.unitPrice),
        lineTotal: number(item.lineTotal),
        maxRefundableAmount: number(item.maxRefundableAmount),
        exchangeTargets: Array.isArray(item.exchangeTargets) ? item.exchangeTargets.map(target => {
          if (!isRecord(target)) return invalid()
          return { skuId: text(target.skuId), skuCode: text(target.skuCode), optionLabel: text(target.optionLabel), regularPrice: number(target.regularPrice), availableQty: integer(target.availableQty) }
        }) : [],
      }
    }),
  }
}

export function parseCustomerReturnSummary(value: unknown): CustomerOrderReturnSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id),
    orderId: text(value.orderId),
    orderNumber: text(value.orderNumber),
    resolutionType: value.resolutionType === undefined ? 'RETURN' : text(value.resolutionType),
    returnStatus: text(value.returnStatus),
    reasonCode: text(value.reasonCode),
    refundAmount: number(value.refundAmount),
    totalItemCount: integer(value.totalItemCount),
    requestedAt: text(value.requestedAt),
    updatedAt: text(value.updatedAt),
    evidenceReference: nullableText(value.evidenceReference),
  }
}

export function parseCustomerReturnDetail(value: unknown): CustomerOrderReturnDetail {
  if (!isRecord(value) || !Array.isArray(value.lines)) return invalid()
  return {
    id: text(value.id),
    orderId: text(value.orderId),
    orderNumber: text(value.orderNumber),
    resolutionType: value.resolutionType === undefined ? 'RETURN' : text(value.resolutionType),
    returnStatus: text(value.returnStatus),
    reasonCode: text(value.reasonCode),
    customerNote: nullableText(value.customerNote),
    refundAmount: number(value.refundAmount),
    shippingFee: typeof value.shippingFee === 'number' ? number(value.shippingFee) : 0,
    orderTotalAmount: typeof value.orderTotalAmount === 'number' ? number(value.orderTotalAmount) : 0,
    requestedAt: text(value.requestedAt),
    decidedAt: nullableText(value.decidedAt),
    decisionNote: nullableText(value.decisionNote),
    receivedAt: nullableText(value.receivedAt),
    refundedAt: nullableText(value.refundedAt),
    evidenceReference: nullableText(value.evidenceReference),
    lines: value.lines.map(parseReturnLine),
    exchangeShipment: value.exchangeShipment == null ? null : (() => {
      if (!isRecord(value.exchangeShipment)) return invalid()
      return { id: text(value.exchangeShipment.id), shipmentStatus: text(value.exchangeShipment.shipmentStatus), redeliveryCount: integer(value.exchangeShipment.redeliveryCount), shippedAt: nullableText(value.exchangeShipment.shippedAt), deliveredAt: nullableText(value.exchangeShipment.deliveredAt) }
    })(),
    createdAt: text(value.createdAt),
    updatedAt: text(value.updatedAt),
  }
}

export function parseCustomerReturnPage(value: unknown): CustomerOrderReturnPage {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    items: value.items.map(parseCustomerReturnSummary),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount),
  }
}

export function parseAdminReturnSummary(value: unknown): AdminOrderReturnSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id),
    orderId: text(value.orderId),
    orderNumber: text(value.orderNumber),
    customerId: text(value.customerId),
    customerEmail: text(value.customerEmail),
    customerFullName: text(value.customerFullName),
    resolutionType: value.resolutionType === undefined ? 'RETURN' : text(value.resolutionType),
    returnStatus: text(value.returnStatus),
    reasonCode: text(value.reasonCode),
    refundAmount: number(value.refundAmount),
    totalItemCount: integer(value.totalItemCount),
    requestedAt: text(value.requestedAt),
    decidedAt: nullableText(value.decidedAt),
    receivedAt: nullableText(value.receivedAt),
    refundedAt: nullableText(value.refundedAt),
    updatedAt: text(value.updatedAt),
  }
}

export function parseAdminReturnDetail(value: unknown): AdminOrderReturnDetail {
  if (!isRecord(value) || !Array.isArray(value.lines)) return invalid()
  return {
    id: text(value.id),
    orderId: text(value.orderId),
    orderNumber: text(value.orderNumber),
    customerId: text(value.customerId),
    customerEmail: text(value.customerEmail),
    customerFullName: text(value.customerFullName),
    resolutionType: value.resolutionType === undefined ? 'RETURN' : text(value.resolutionType),
    returnStatus: text(value.returnStatus),
    reasonCode: text(value.reasonCode),
    customerNote: nullableText(value.customerNote),
    refundAmount: number(value.refundAmount),
    requestedAt: text(value.requestedAt),
    decidedBy: nullableText(value.decidedBy),
    decidedByEmail: nullableText(value.decidedByEmail),
    decidedAt: nullableText(value.decidedAt),
    decisionNote: nullableText(value.decisionNote),
    receivedBy: nullableText(value.receivedBy),
    receivedByEmail: nullableText(value.receivedByEmail),
    receivedAt: nullableText(value.receivedAt),
    receivedEvidence: nullableText(value.receivedEvidence),
    inspectedBy: nullableText(value.inspectedBy),
    inspectedByEmail: nullableText(value.inspectedByEmail),
    inspectedAt: nullableText(value.inspectedAt),
    inspectionNote: nullableText(value.inspectionNote),
    refundedAt: nullableText(value.refundedAt),
    evidenceReference: nullableText(value.evidenceReference),
    lines: value.lines.map(parseReturnLine),
    exchangeShipment: value.exchangeShipment == null ? null : (() => {
      if (!isRecord(value.exchangeShipment)) return invalid()
      return { id: text(value.exchangeShipment.id), shipmentStatus: text(value.exchangeShipment.shipmentStatus), redeliveryCount: integer(value.exchangeShipment.redeliveryCount), shippedAt: nullableText(value.exchangeShipment.shippedAt), deliveredAt: nullableText(value.exchangeShipment.deliveredAt) }
    })(),
    revision: integer(value.revision),
    createdAt: text(value.createdAt),
    updatedAt: text(value.updatedAt),
  }
}

export function parseAdminReturnPage(value: unknown): AdminOrderReturnPage {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    items: value.items.map(parseAdminReturnSummary),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount),
  }
}

export function parseStaffReturnSummary(value: unknown): StaffOrderReturnSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id),
    orderId: text(value.orderId),
    orderNumber: text(value.orderNumber),
    resolutionType: value.resolutionType === undefined ? 'RETURN' : text(value.resolutionType),
    returnStatus: text(value.returnStatus),
    reasonCode: text(value.reasonCode),
    refundAmount: number(value.refundAmount),
    totalItemCount: integer(value.totalItemCount),
    requestedAt: text(value.requestedAt),
    decidedAt: nullableText(value.decidedAt),
    decisionNote: nullableText(value.decisionNote),
  }
}

export function parseStaffReturnPage(value: unknown): StaffOrderReturnPage {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    items: value.items.map(parseStaffReturnSummary),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount),
  }
}

async function getCsrfToken(): Promise<string> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return csrf.requestToken
}

// -----------------------------------------------------------------------------
// Customer API
// -----------------------------------------------------------------------------

export async function getReturnEligibility(orderId: string, signal?: AbortSignal): Promise<OrderReturnEligibility> {
  const res = await apiRequest(`/orders/${encodeURIComponent(orderId)}/return-eligibility`, {
    signal,
    cache: 'no-store',
  })
  return parseReturnEligibility(res)
}

export async function createReturnRequest(
  orderId: string,
  request: CreateOrderReturnRequest
): Promise<CustomerOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/orders/${encodeURIComponent(orderId)}/returns`, {
    method: 'POST',
    body: JSON.stringify(request),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseCustomerReturnDetail(res)
}

export async function getCustomerReturns(
  page = 1,
  pageSize = 20,
  status?: string,
  signal?: AbortSignal
): Promise<CustomerOrderReturnPage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (status) params.set('status', status)
  const res = await apiRequest(`/account/returns?${params.toString()}`, { signal, cache: 'no-store' })
  return parseCustomerReturnPage(res)
}

export async function getCustomerReturnById(returnId: string, signal?: AbortSignal): Promise<CustomerOrderReturnDetail> {
  const res = await apiRequest(`/account/returns/${encodeURIComponent(returnId)}`, { signal, cache: 'no-store' })
  return parseCustomerReturnDetail(res)
}

export async function cancelReturnRequest(returnId: string): Promise<CustomerOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/account/returns/${encodeURIComponent(returnId)}/cancel`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseCustomerReturnDetail(res)
}

export async function customerReturnsLoader(args: LoaderFunctionArgs): Promise<CustomerOrderReturnPage> {
  await requireRole(['CUSTOMER'])(args)
  const url = new URL(args.request.url)
  const page = Number(url.searchParams.get('page') ?? '1') || 1
  const status = url.searchParams.get('status') ?? undefined
  return getCustomerReturns(page, 20, status, args.request.signal)
}

// -----------------------------------------------------------------------------
// Admin API
// -----------------------------------------------------------------------------

export async function getAdminReturns(
  query: { page?: number; pageSize?: number; status?: string; q?: string } = {},
  signal?: AbortSignal
): Promise<AdminOrderReturnPage> {
  const params = new URLSearchParams()
  if (query.page) params.set('page', String(query.page))
  if (query.pageSize) params.set('pageSize', String(query.pageSize))
  if (query.status) params.set('status', query.status)
  if (query.q) params.set('q', query.q)
  const res = await apiRequest(`/admin/returns?${params.toString()}`, { signal, cache: 'no-store' })
  return parseAdminReturnPage(res)
}

export async function getAdminReturnById(returnId: string, signal?: AbortSignal): Promise<AdminOrderReturnDetail> {
  const res = await apiRequest(`/admin/returns/${encodeURIComponent(returnId)}`, { signal, cache: 'no-store' })
  return parseAdminReturnDetail(res)
}

export async function approveReturn(returnId: string, decisionNote?: string): Promise<AdminOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/returns/${encodeURIComponent(returnId)}/approve`, {
    method: 'POST',
    body: JSON.stringify({ decisionNote: decisionNote ?? null }),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminReturnDetail(res)
}

export async function rejectReturn(returnId: string, decisionNote: string): Promise<AdminOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/returns/${encodeURIComponent(returnId)}/reject`, {
    method: 'POST',
    body: JSON.stringify({ decisionNote }),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminReturnDetail(res)
}

export async function processRefund(returnId: string, evidenceReference: string): Promise<AdminOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/returns/${encodeURIComponent(returnId)}/refund`, {
    method: 'POST',
    body: JSON.stringify({ evidenceReference }),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminReturnDetail(res)
}

// -----------------------------------------------------------------------------
// Staff API
// -----------------------------------------------------------------------------

export async function getApprovedReturnsQueue(
  query: { page?: number; pageSize?: number; q?: string } = {},
  signal?: AbortSignal
): Promise<StaffOrderReturnPage> {
  const params = new URLSearchParams()
  if (query.page) params.set('page', String(query.page))
  if (query.pageSize) params.set('pageSize', String(query.pageSize))
  if (query.q) params.set('q', query.q)
  const res = await apiRequest(`/staff/returns/approved?${params.toString()}`, { signal, cache: 'no-store' })
  return parseStaffReturnPage(res)
}

export async function receiveReturn(returnId: string, receivedEvidence: string): Promise<AdminOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/staff/returns/${encodeURIComponent(returnId)}/receive`, {
    method: 'POST',
    body: JSON.stringify({ receivedEvidence }),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminReturnDetail(res)
}

export async function getStaffReturnById(returnId: string, signal?: AbortSignal): Promise<AdminOrderReturnDetail> {
  const res = await apiRequest(`/staff/returns/${encodeURIComponent(returnId)}`, { signal, cache: 'no-store' })
  return parseAdminReturnDetail(res)
}

export async function inspectReturn(
  returnId: string,
  request: {
    lines: Array<{ returnLineId: string; restockQty: number; quarantineQty: number; damagedQty: number; scrappedQty: number }>
    inspectionNote?: string | null
  },
): Promise<AdminOrderReturnDetail> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/staff/returns/${encodeURIComponent(returnId)}/inspect`, {
    method: 'POST',
    body: JSON.stringify(request),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminReturnDetail(res)
}

export type ExchangeFulfillment = {
  returnRequestId: string; shipmentId: string; orderId: string; orderNumber: string; returnStatus: string; shipmentStatus: string
  redeliveryCount: number; shippedAt: string | null; deliveredAt: string | null
  lines: Array<{ targetSkuId: string; skuCode: string; optionLabel: string; quantity: number }>
}

function parseExchangeFulfillment(value: unknown): ExchangeFulfillment {
  if (!isRecord(value) || !Array.isArray(value.lines)) return invalid()
  return {
    returnRequestId: text(value.returnRequestId), shipmentId: text(value.shipmentId), orderId: text(value.orderId), orderNumber: text(value.orderNumber),
    returnStatus: text(value.returnStatus), shipmentStatus: text(value.shipmentStatus), redeliveryCount: integer(value.redeliveryCount),
    shippedAt: nullableText(value.shippedAt), deliveredAt: nullableText(value.deliveredAt),
    lines: value.lines.map(line => {
      if (!isRecord(line)) return invalid()
      return { targetSkuId: text(line.targetSkuId), skuCode: text(line.skuCode), optionLabel: text(line.optionLabel), quantity: integer(line.quantity, 1) }
    }),
  }
}

async function exchangeMutation(path: string, body?: unknown): Promise<ExchangeFulfillment> {
  const token = await getCsrfToken()
  return parseExchangeFulfillment(await apiRequest(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body), headers: { 'X-CSRF-TOKEN': token } }))
}

export async function getExchangeShipments(signal?: AbortSignal): Promise<ExchangeFulfillment[]> {
  const value = await apiRequest('/staff/exchange-shipments', { signal, cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseExchangeFulfillment)
}
export const packExchangeShipment = (id: string) => exchangeMutation(`/staff/exchange-shipments/${encodeURIComponent(id)}/pack`)
export const handoverExchangeShipment = (id: string) => exchangeMutation(`/staff/exchange-shipments/${encodeURIComponent(id)}/handover`)
export const recordExchangeDelivery = (id: string, result: 'DELIVERED' | 'FAILED', note?: string) => exchangeMutation(`/staff/exchange-shipments/${encodeURIComponent(id)}/delivery-result`, { result, note: note || null })
export const retryExchangeDelivery = (id: string) => exchangeMutation(`/staff/exchange-shipments/${encodeURIComponent(id)}/retry`)
export const returnExchangeStock = (id: string, evidence: string) => exchangeMutation(`/staff/exchange-shipments/${encodeURIComponent(id)}/return-stock`, { evidence })

