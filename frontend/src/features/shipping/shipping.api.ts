import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import type { CheckoutLine } from '../checkout/checkout.api'

export type ShippingQuote = {
  shippingMethodId: string
  code: string
  name: string
  description: string | null
  shippingFee: number
  baseFee: number
  provinceSurcharge: number
  freeShippingApplied: boolean
  freeShippingThreshold: number | null
  estimatedMinDays: number
  estimatedMaxDays: number
  methodRevision: number
  isDefault: boolean
}

export type ShippingQuoteResult = {
  shippingAddressId: string
  destinationProvinceKey: string
  merchandiseSubtotal: number
  quotes: ShippingQuote[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' && value.trim().length > 0 ? value : invalid() }
function money(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown, min = 0): number {
  const parsed = money(value)
  return Number.isSafeInteger(parsed) && parsed >= min ? parsed : invalid()
}

function parseQuote(value: unknown): ShippingQuote {
  if (!isRecord(value) || typeof value.freeShippingApplied !== 'boolean' || typeof value.isDefault !== 'boolean') return invalid()
  const min = integer(value.estimatedMinDays)
  const max = integer(value.estimatedMaxDays)
  if (max < min) return invalid()
  return {
    shippingMethodId: text(value.shippingMethodId),
    code: text(value.code),
    name: text(value.name),
    description: value.description == null ? null : text(value.description),
    shippingFee: money(value.shippingFee),
    baseFee: money(value.baseFee),
    provinceSurcharge: money(value.provinceSurcharge),
    freeShippingApplied: value.freeShippingApplied,
    freeShippingThreshold: value.freeShippingThreshold == null ? null : money(value.freeShippingThreshold),
    estimatedMinDays: min,
    estimatedMaxDays: max,
    methodRevision: integer(value.methodRevision, 1),
    isDefault: value.isDefault,
  }
}

export function parseShippingQuoteResult(value: unknown): ShippingQuoteResult {
  if (!isRecord(value) || !Array.isArray(value.quotes)) return invalid()
  return {
    shippingAddressId: text(value.shippingAddressId),
    destinationProvinceKey: text(value.destinationProvinceKey),
    merchandiseSubtotal: money(value.merchandiseSubtotal),
    quotes: value.quotes.map(parseQuote),
  }
}

export async function requestShippingQuotes(
  shippingAddressId: string,
  lines: CheckoutLine[],
  signal?: AbortSignal,
): Promise<ShippingQuoteResult> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store', signal })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return parseShippingQuoteResult(await apiRequest('/shipping/quotes', {
    method: 'POST',
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    body: JSON.stringify({
      shippingAddressId,
      lines: lines.map(line => ({
        productSkuId: line.productSkuId,
        quantity: line.quantity,
        flashSaleOfferId: line.flashSaleOfferId ?? null,
      })),
    }),
  }))
}
