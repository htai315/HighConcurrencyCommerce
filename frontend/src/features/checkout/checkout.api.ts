import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type CheckoutPaymentMethod = 'COD' | 'PAYOS'
export type CheckoutLine = { productSkuId: string; quantity: number; flashSaleOfferId?: string | null }
export type CheckoutShippingSelection = { shippingMethodId: string; methodRevision: number; shippingFee: number }
export type CheckoutShippingMethod = {
  shippingMethodId: string; code: string; name: string; estimatedMinDays: number; estimatedMaxDays: number; revision: number; destinationProvinceKey: string
}
export type CheckoutResult = {
  orderId: string
  orderNumber: string
  orderStatus: string
  paymentMethod: CheckoutPaymentMethod
  paymentStatus: string
  totalAmount: number
  subtotalAmount: number
  voucherDiscountAmount: number
  voucherCode: string | null
  shippingFee: number
  shippingMethod: CheckoutShippingMethod | null
  deadlineAt: string
  shippingAddress: { recipientName: string; phone: string; addressLine: string; ward: string | null; province: string }
  items: Array<{ productSkuId: string; productTitle: string; skuCode: string; optionLabel: string; quantity: number; unitPrice: number; lineTotal: number }>
}

const PENDING_KEY = 'hcc.checkout-intent.v1'

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function amount(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown): number { const parsed = amount(value); return Number.isSafeInteger(parsed) ? parsed : invalid() }
function address(value: unknown): CheckoutResult['shippingAddress'] {
  if (!isRecord(value)) return invalid()
  return { recipientName: text(value.recipientName), phone: text(value.phone), addressLine: text(value.addressLine), ward: value.ward === null ? null : text(value.ward), province: text(value.province) }
}
function shippingMethod(value: unknown): CheckoutShippingMethod | null {
  if (value == null) return null
  if (!isRecord(value)) return invalid()
  return {
    shippingMethodId: text(value.shippingMethodId), code: text(value.code), name: text(value.name),
    estimatedMinDays: integer(value.estimatedMinDays), estimatedMaxDays: integer(value.estimatedMaxDays),
    revision: integer(value.revision), destinationProvinceKey: text(value.destinationProvinceKey),
  }
}
function parse(value: unknown): CheckoutResult {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  const paymentMethod = text(value.paymentMethod)
  if (paymentMethod !== 'COD' && paymentMethod !== 'PAYOS') return invalid()
  return {
    orderId: text(value.orderId), orderNumber: text(value.orderNumber), orderStatus: text(value.orderStatus),
    paymentMethod, paymentStatus: text(value.paymentStatus), totalAmount: amount(value.totalAmount),
    subtotalAmount: typeof value.subtotalAmount === 'number' ? amount(value.subtotalAmount) : amount(value.totalAmount),
    voucherDiscountAmount: typeof value.voucherDiscountAmount === 'number' ? amount(value.voucherDiscountAmount) : 0,
    voucherCode: typeof value.voucherCode === 'string' && value.voucherCode.trim().length > 0 ? text(value.voucherCode) : null,
    shippingFee: typeof value.shippingFee === 'number' ? amount(value.shippingFee) : 0,
    shippingMethod: shippingMethod(value.shippingMethod),
    deadlineAt: text(value.deadlineAt), shippingAddress: address(value.shippingAddress),
    items: value.items.map(entry => {
      if (!isRecord(entry)) return invalid()
      return {
        productSkuId: text(entry.productSkuId), productTitle: text(entry.productTitle), skuCode: text(entry.skuCode),
        optionLabel: text(entry.optionLabel), quantity: integer(entry.quantity), unitPrice: amount(entry.unitPrice), lineTotal: amount(entry.lineTotal),
      }
    }),
  }
}

function intentKey(fingerprint: string): string {
  try {
    const stored: unknown = JSON.parse(sessionStorage.getItem(PENDING_KEY) ?? 'null')
    if (isRecord(stored) && stored.fingerprint === fingerprint && typeof stored.key === 'string') return stored.key
  } catch { /* replace malformed local state below */ }
  const key = crypto.randomUUID()
  sessionStorage.setItem(PENDING_KEY, JSON.stringify({ fingerprint, key }))
  return key
}

export function clearCheckoutIntent() { sessionStorage.removeItem(PENDING_KEY) }

export async function createCheckout(
  shippingAddressId: string,
  paymentMethod: CheckoutPaymentMethod,
  lines: CheckoutLine[],
  shipping: CheckoutShippingSelection,
  voucherCode?: string | null,
): Promise<CheckoutResult> {
  const normalizedLines = [...lines].sort((a, b) => a.productSkuId.localeCompare(b.productSkuId))
  const normalizedVoucherCode = voucherCode && voucherCode.trim().length > 0 ? voucherCode.trim().toUpperCase() : null
  const body: {
    shippingAddressId: string
    paymentMethod: CheckoutPaymentMethod
    shippingMethodId: string
    shippingMethodRevision: number
    quotedShippingFee: number
    lines: Array<{ productSkuId: string; quantity: number; flashSaleOfferId: string | null }>
    voucherCode?: string
  } = {
    shippingAddressId,
    paymentMethod,
    shippingMethodId: shipping.shippingMethodId,
    shippingMethodRevision: shipping.methodRevision,
    quotedShippingFee: shipping.shippingFee,
    lines: normalizedLines.map(line => ({
      productSkuId: line.productSkuId,
      quantity: line.quantity,
      flashSaleOfferId: line.flashSaleOfferId ?? null,
    })),
  }
  if (normalizedVoucherCode) {
    body.voucherCode = normalizedVoucherCode
  }
  const fingerprint = JSON.stringify(body)
  const key = intentKey(fingerprint)
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  const result = parse(await apiRequest('/checkouts', {
    method: 'POST', body: JSON.stringify(body),
    headers: { 'X-CSRF-TOKEN': csrf.requestToken, 'Idempotency-Key': key },
  }))
  clearCheckoutIntent()
  return result
}
