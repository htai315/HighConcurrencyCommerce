import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type FulfillmentItem = { productSkuId: string; productTitle: string; skuCode: string; optionLabel: string; quantity: number }
export type FulfillmentOrder = {
  orderId: string
  orderNumber: string
  orderStatus: string
  shipmentId: string | null
  shipmentStatus: string | null
  paymentStatus: string
  customerName: string
  totalAmount: number
  recipientName: string
  recipientPhone: string
  shippingAddress: string
  shippedAt: string | null
  deliveredAt: string | null
  redeliveryCount: number
  returnStockCompleted: boolean
  items: FulfillmentItem[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown): number { const parsed = number(value); return Number.isSafeInteger(parsed) ? parsed : invalid() }
function item(value: unknown): FulfillmentItem {
  if (!isRecord(value)) return invalid()
  return { productSkuId: text(value.productSkuId), productTitle: text(value.productTitle), skuCode: text(value.skuCode), optionLabel: text(value.optionLabel), quantity: integer(value.quantity) }
}
function order(value: unknown): FulfillmentOrder {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    orderId: text(value.orderId), orderNumber: text(value.orderNumber), orderStatus: text(value.orderStatus),
    shipmentId: value.shipmentId === null ? null : text(value.shipmentId), shipmentStatus: value.shipmentStatus === null ? null : text(value.shipmentStatus),
    paymentStatus: text(value.paymentStatus), customerName: text(value.customerName), totalAmount: number(value.totalAmount),
    recipientName: text(value.recipientName), recipientPhone: text(value.recipientPhone), shippingAddress: text(value.shippingAddress),
    shippedAt: value.shippedAt === null ? null : text(value.shippedAt), deliveredAt: value.deliveredAt === null ? null : text(value.deliveredAt),
    redeliveryCount: integer(value.redeliveryCount), returnStockCompleted: typeof value.returnStockCompleted === 'boolean' ? value.returnStockCompleted : invalid(),
    items: value.items.map(item),
  }
}
async function mutation(path: string, body?: unknown): Promise<FulfillmentOrder> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return order(await apiRequest(path, {
    method: 'POST', body: body === undefined ? undefined : JSON.stringify(body),
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  }))
}

export async function getFulfillmentQueue(): Promise<FulfillmentOrder[]> {
  const value = await apiRequest('/staff/shipments', { cache: 'no-store' })
  return Array.isArray(value) ? value.map(order) : invalid()
}
export const startFulfillment = (orderId: string) => mutation(`/staff/orders/${encodeURIComponent(orderId)}/start-fulfillment`)
export const packShipment = (shipmentId: string) => mutation(`/staff/shipments/${encodeURIComponent(shipmentId)}/pack`)
export const handoverShipment = (shipmentId: string) => mutation(`/staff/shipments/${encodeURIComponent(shipmentId)}/handover`)
export const deliverCodShipment = (shipmentId: string, amount: number, reference: string) => mutation(
  `/staff/shipments/${encodeURIComponent(shipmentId)}/delivery-result`,
  { result: 'DELIVERED', codCollectedAmount: amount, collectionReference: reference },
)
export const failDelivery = (shipmentId: string, reason: string) => mutation(
  `/staff/shipments/${encodeURIComponent(shipmentId)}/delivery-result`,
  { result: 'FAILED', failureReason: reason },
)
export const retryDelivery = (shipmentId: string) => mutation(`/staff/shipments/${encodeURIComponent(shipmentId)}/retry`)
export const returnStock = (shipmentId: string, evidence: string) => mutation(
  `/staff/shipments/${encodeURIComponent(shipmentId)}/return-stock`,
  { receivedEvidence: evidence },
)
