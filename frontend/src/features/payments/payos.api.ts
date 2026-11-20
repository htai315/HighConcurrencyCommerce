import type { LoaderFunctionArgs } from 'react-router-dom'
import { requireRole } from '../auth/auth.api'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type PaymentProviders = { payOs: { enabled: boolean } }
export type PayOsAttempt = {
  attemptId: string
  attemptNo: number
  status: string
  checkoutUrl: string | null
  expiresAt: string
  requiresProviderQuery: boolean
}
export type PayOsPaymentStatus = {
  orderId: string
  orderNumber: string
  orderStatus: string
  paymentStatus: string
  expectedAmount: number
  deadlineAt: string
  latestAttempt: PayOsAttempt | null
}

const RETURN_ORDER_KEY = 'hcc.payos-return-order.v1'

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function integer(value: unknown): number { return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : invalid() }
function amount(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function nullableText(value: unknown): string | null { return value === null ? null : text(value) }

function attempt(value: unknown): PayOsAttempt {
  if (!isRecord(value) || typeof value.requiresProviderQuery !== 'boolean') return invalid()
  const checkoutUrl = nullableText(value.checkoutUrl)
  if (checkoutUrl) {
    const url = new URL(checkoutUrl)
    if (url.protocol !== 'https:' || url.hostname !== 'pay.payos.vn') return invalid()
  }
  return {
    attemptId: text(value.attemptId), attemptNo: integer(value.attemptNo), status: text(value.status),
    checkoutUrl, expiresAt: text(value.expiresAt), requiresProviderQuery: value.requiresProviderQuery,
  }
}

function paymentStatus(value: unknown): PayOsPaymentStatus {
  if (!isRecord(value)) return invalid()
  return {
    orderId: text(value.orderId), orderNumber: text(value.orderNumber), orderStatus: text(value.orderStatus),
    paymentStatus: text(value.paymentStatus), expectedAmount: amount(value.expectedAmount), deadlineAt: text(value.deadlineAt),
    latestAttempt: value.latestAttempt === null ? null : attempt(value.latestAttempt),
  }
}

async function csrfToken(): Promise<string> {
  const value = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(value) || typeof value.requestToken !== 'string') return invalid()
  return value.requestToken
}

export async function getPaymentProviders(signal?: AbortSignal): Promise<PaymentProviders> {
  const value = await apiRequest('/payments/providers', { signal, cache: 'no-store' })
  if (!isRecord(value) || !isRecord(value.payOs) || typeof value.payOs.enabled !== 'boolean') return invalid()
  return { payOs: { enabled: value.payOs.enabled } }
}

export async function createPayOsAttempt(orderId: string): Promise<PayOsAttempt> {
  const csrf = await csrfToken()
  return attempt(await apiRequest(`/customer/orders/${encodeURIComponent(orderId)}/payment/payos-attempts`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': csrf },
  }))
}

export async function getPayOsPaymentStatus(orderId: string, signal?: AbortSignal): Promise<PayOsPaymentStatus> {
  return paymentStatus(await apiRequest(`/customer/orders/${encodeURIComponent(orderId)}/payment`, {
    signal, cache: 'no-store',
  }))
}

export async function cancelPayOsPayment(orderId: string, reason: string): Promise<void> {
  const csrf = await csrfToken()
  await apiRequest(`/customer/orders/${encodeURIComponent(orderId)}/payment/cancel`, {
    method: 'POST', body: JSON.stringify({ reason }), headers: { 'X-CSRF-TOKEN': csrf },
  })
}

export function rememberPayOsReturnOrder(orderId: string): void {
  sessionStorage.setItem(RETURN_ORDER_KEY, orderId)
}

export function readPayOsReturnOrder(): string | null {
  const value = sessionStorage.getItem(RETURN_ORDER_KEY)
  return value && /^[0-9a-f-]{36}$/i.test(value) ? value : null
}

export function redirectToPayOs(checkoutUrl: string): void {
  const url = new URL(checkoutUrl)
  if (url.protocol !== 'https:' || url.hostname !== 'pay.payos.vn') return invalid()
  window.location.assign(url.href)
}

export async function payOsReturnLoader(args: LoaderFunctionArgs) {
  await requireRole(['CUSTOMER'])(args)
  const url = new URL(args.request.url)
  const orderId = readPayOsReturnOrder()
  let initialStatus: PayOsPaymentStatus | null = null
  let initialLoadFailed = false
  if (orderId) {
    try {
      initialStatus = await getPayOsPaymentStatus(orderId, args.request.signal)
    } catch (error) {
      if (args.request.signal.aborted) throw error
      // The authenticated PayOS return page owns retry UX. A transient status
      // failure is not an authentication failure and must not hit AuthErrorPage.
      initialLoadFailed = true
    }
  }
  return {
    orderId,
    initialStatus,
    initialLoadFailed,
    providerCancelled: url.searchParams.get('cancel') === '1' || url.searchParams.get('status') === 'CANCELLED',
  }
}
