import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { requireRole } from '../auth/auth.api'

export type CustomerOrderItem = {
  productSkuId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  quantity: number
  unitPrice: number
  lineTotal: number
}

export type CustomerDeliveryAttempt = {
  attemptNo: number
  result: string
  note: string | null
  finishedAt: string | null
}

export type CustomerOrder = {
  id: string
  orderNumber: string
  orderStatus: string
  paymentMethod: string
  paymentStatus: string
  reservationStatus: string
  totalAmount: number
  createdAt: string
  deadlineAt: string
  confirmedAt: string | null
  closedReason: string | null
  recipientName: string
  recipientPhone: string
  shippingAddress: string
  shipmentId: string | null
  shipmentStatus: string | null
  shippedAt: string | null
  deliveredAt: string | null
  redeliveryCount: number
  canCancel: boolean
  cancellationReason: string | null
  items: CustomerOrderItem[]
  deliveryAttempts: CustomerDeliveryAttempt[]
}

export type CustomerOrderPage = {
  items: CustomerOrder[]
  page: number
  pageSize: number
  totalCount: number
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function nullableText(value: unknown): string | null { return value === null ? null : text(value) }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) ? value : invalid() }
function integer(value: unknown, min = 0): number {
  const result = number(value)
  return Number.isSafeInteger(result) && result >= min ? result : invalid()
}

function parseItem(value: unknown): CustomerOrderItem {
  if (!isRecord(value)) return invalid()
  return {
    productSkuId: text(value.productSkuId), productTitle: text(value.productTitle),
    skuCode: text(value.skuCode), optionLabel: text(value.optionLabel), quantity: integer(value.quantity, 1),
    unitPrice: number(value.unitPrice), lineTotal: number(value.lineTotal),
  }
}

function parseAttempt(value: unknown): CustomerDeliveryAttempt {
  if (!isRecord(value)) return invalid()
  return {
    attemptNo: integer(value.attemptNo, 1), result: text(value.result),
    note: nullableText(value.note), finishedAt: nullableText(value.finishedAt),
  }
}

export function parseCustomerOrder(value: unknown): CustomerOrder {
  if (!isRecord(value) || typeof value.canCancel !== 'boolean'
    || !Array.isArray(value.items) || !Array.isArray(value.deliveryAttempts)) return invalid()
  return {
    id: text(value.id), orderNumber: text(value.orderNumber), orderStatus: text(value.orderStatus),
    paymentMethod: text(value.paymentMethod), paymentStatus: text(value.paymentStatus),
    reservationStatus: text(value.reservationStatus), totalAmount: number(value.totalAmount),
    createdAt: text(value.createdAt), deadlineAt: text(value.deadlineAt), confirmedAt: nullableText(value.confirmedAt),
    closedReason: nullableText(value.closedReason), recipientName: text(value.recipientName),
    recipientPhone: text(value.recipientPhone), shippingAddress: text(value.shippingAddress),
    shipmentId: nullableText(value.shipmentId), shipmentStatus: nullableText(value.shipmentStatus),
    shippedAt: nullableText(value.shippedAt), deliveredAt: nullableText(value.deliveredAt),
    redeliveryCount: integer(value.redeliveryCount), canCancel: value.canCancel,
    cancellationReason: nullableText(value.cancellationReason), items: value.items.map(parseItem),
    deliveryAttempts: value.deliveryAttempts.map(parseAttempt),
  }
}

function parsePage(value: unknown): CustomerOrderPage {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    items: value.items.map(parseCustomerOrder), page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1), totalCount: integer(value.totalCount),
  }
}

export async function ordersLoader(args: LoaderFunctionArgs): Promise<CustomerOrderPage> {
  await requireRole(['CUSTOMER'])(args)
  return parsePage(await apiRequest('/orders?page=1&pageSize=50', { signal: args.request.signal, cache: 'no-store' }))
}

export async function cancelCustomerOrder(id: string, reason: string): Promise<CustomerOrder> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return parseCustomerOrder(await apiRequest(`/orders/${encodeURIComponent(id)}/cancel`, {
    method: 'POST', body: JSON.stringify({ reason }), headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  }))
}
