import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type ReconciliationSummary = {
  id: string; paymentId: string; orderId: string; orderNumber: string; customerEmail: string
  reason: string; caseStatus: string; expectedAmount: number; paymentStatus: string
  assignedTo: string | null; assignedToName: string | null; createdAt: string; updatedAt: string
}
export type ReconciliationTransaction = {
  id: string; providerTransactionId: string | null; amount: number; transactionStatus: string
  providerPaidAt: string | null; verifiedAt: string
}
export type ReconciliationDetail = ReconciliationSummary & {
  customerId: string; customerName: string; assignedAt: string | null; resolvedBy: string | null
  resolvedByName: string | null; resolvedAt: string | null; note: string | null
  transactions: ReconciliationTransaction[]
  manualRefund: null | { id: string; transactionId: string; amount: number; evidenceReference: string; refundStatus: string; refundedAt: string | null; adminName: string }
}
export type ReconciliationPage = { items: ReconciliationSummary[]; page: number; pageSize: number; totalCount: number }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
const text = (value: unknown) => typeof value === 'string' ? value : invalid()
const nullableText = (value: unknown) => value === null ? null : text(value)
const number = (value: unknown) => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid()

function summary(value: unknown): ReconciliationSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id), paymentId: text(value.paymentId), orderId: text(value.orderId), orderNumber: text(value.orderNumber),
    customerEmail: text(value.customerEmail), reason: text(value.reason), caseStatus: text(value.caseStatus),
    expectedAmount: number(value.expectedAmount), paymentStatus: text(value.paymentStatus), assignedTo: nullableText(value.assignedTo),
    assignedToName: nullableText(value.assignedToName), createdAt: text(value.createdAt), updatedAt: text(value.updatedAt),
  }
}
function transaction(value: unknown): ReconciliationTransaction {
  if (!isRecord(value)) return invalid()
  return { id: text(value.id), providerTransactionId: nullableText(value.providerTransactionId), amount: number(value.amount), transactionStatus: text(value.transactionStatus), providerPaidAt: nullableText(value.providerPaidAt), verifiedAt: text(value.verifiedAt) }
}
function detail(value: unknown): ReconciliationDetail {
  if (!isRecord(value) || !Array.isArray(value.transactions)) return invalid()
  const base = summary(value)
  const refund = value.manualRefund
  return {
    ...base, customerId: text(value.customerId), customerName: text(value.customerName), assignedAt: nullableText(value.assignedAt),
    resolvedBy: nullableText(value.resolvedBy), resolvedByName: nullableText(value.resolvedByName), resolvedAt: nullableText(value.resolvedAt),
    note: nullableText(value.note), transactions: value.transactions.map(transaction),
    manualRefund: refund === null ? null : isRecord(refund) ? {
      id: text(refund.id), transactionId: text(refund.transactionId), amount: number(refund.amount),
      evidenceReference: text(refund.evidenceReference), refundStatus: text(refund.refundStatus),
      refundedAt: nullableText(refund.refundedAt), adminName: text(refund.adminName),
    } : invalid(),
  }
}
async function csrf(): Promise<string> {
  const value = await apiRequest('/auth/csrf', { cache: 'no-store' })
  return isRecord(value) && typeof value.requestToken === 'string' ? value.requestToken : invalid()
}
async function mutation(path: string, body?: unknown): Promise<ReconciliationDetail> {
  const token = await csrf()
  return detail(await apiRequest(path, { method: 'POST', headers: { 'X-CSRF-TOKEN': token }, body: body === undefined ? undefined : JSON.stringify(body) }))
}
export async function getReconciliationCases(status = ''): Promise<ReconciliationPage> {
  const raw = await apiRequest(`/admin/payos-reconciliation?page=1&pageSize=100${status ? `&status=${encodeURIComponent(status)}` : ''}`, { cache: 'no-store' })
  if (!isRecord(raw) || !Array.isArray(raw.items)) return invalid()
  return { items: raw.items.map(summary), page: number(raw.page), pageSize: number(raw.pageSize), totalCount: number(raw.totalCount) }
}
export const getReconciliationCase = async (id: string) => detail(await apiRequest(`/admin/payos-reconciliation/${encodeURIComponent(id)}`, { cache: 'no-store' }))
export const claimReconciliationCase = (id: string) => mutation(`/admin/payos-reconciliation/${encodeURIComponent(id)}/claim`)
export const resolveReconciliationCase = (id: string, note: string) => mutation(`/admin/payos-reconciliation/${encodeURIComponent(id)}/resolve`, { note })
export const refundReconciliationCase = (id: string, evidenceReference: string, note: string) => mutation(`/admin/payos-reconciliation/${encodeURIComponent(id)}/manual-refund`, { evidenceReference, note })
