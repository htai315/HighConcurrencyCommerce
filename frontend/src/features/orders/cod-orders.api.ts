import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type CodOrderItem = {
  productSkuId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  quantity: number
  unitPrice: number
  lineTotal: number
}
export type CodOrder = {
  orderId: string
  orderNumber: string
  orderStatus: string
  paymentStatus: string
  customerId: string
  customerName: string
  customerEmail: string
  totalAmount: number
  createdAt: string
  deadlineAt: string
  recipientName: string
  recipientPhone: string
  shippingAddress: string
  items: CodOrderItem[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown): number { const parsed = number(value); return Number.isSafeInteger(parsed) ? parsed : invalid() }
function item(value: unknown): CodOrderItem {
  if (!isRecord(value)) return invalid()
  return {
    productSkuId: text(value.productSkuId), productTitle: text(value.productTitle), skuCode: text(value.skuCode),
    optionLabel: text(value.optionLabel), quantity: integer(value.quantity), unitPrice: number(value.unitPrice), lineTotal: number(value.lineTotal),
  }
}
function order(value: unknown): CodOrder {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    orderId: text(value.orderId), orderNumber: text(value.orderNumber), orderStatus: text(value.orderStatus),
    paymentStatus: text(value.paymentStatus), customerId: text(value.customerId), customerName: text(value.customerName),
    customerEmail: text(value.customerEmail), totalAmount: number(value.totalAmount), createdAt: text(value.createdAt),
    deadlineAt: text(value.deadlineAt), recipientName: text(value.recipientName), recipientPhone: text(value.recipientPhone),
    shippingAddress: text(value.shippingAddress), items: value.items.map(item),
  }
}

export async function getPendingCodOrders(): Promise<CodOrder[]> {
  const value = await apiRequest('/staff/orders/cod-pending', { cache: 'no-store' })
  return Array.isArray(value) ? value.map(order) : invalid()
}

export async function confirmCodOrder(orderId: string): Promise<CodOrder> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return order(await apiRequest(`/staff/orders/${encodeURIComponent(orderId)}/confirm-cod`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  }))
}
