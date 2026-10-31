import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type StaffTasks = {
  pendingCodCount: number; shipmentsToPackCount: number; shipmentsToHandoverCount: number
  deliveryFailuresCount: number; lowStockSkuCount: number; notificationsUnreadCount: number
}
export type RevenueReport = {
  from: string; to: string; revenue: number; completedOrderCount: number; averageOrderValue: number
  days: { date: string; revenue: number; completedOrderCount: number }[]
  paymentMethods: { paymentMethod: string; revenue: number; completedOrderCount: number }[]
}
export type LowStockItem = {
  productSkuId: string; productTitle: string; skuCode: string; optionLabel: string
  availableQty: number; physicalQty: number; reservedQty: number; allocatedQty: number
}
export type LowStockReport = { threshold: number; items: LowStockItem[] }
export type AuditLogItem = {
  id: string; actorId: string | null; actorType: string; actorName: string | null; action: string
  entityType: string; entityId: string; beforeData: string | null; afterData: string | null
  correlationId: string | null; occurredAt: string
}
export type AuditPage = { items: AuditLogItem[]; page: number; pageSize: number; totalCount: number }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function nullableText(value: unknown): string | null { return value === null ? null : text(value) }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown): number { const parsed = number(value); return Number.isSafeInteger(parsed) ? parsed : invalid() }
function record(value: unknown): Record<string, unknown> { return isRecord(value) ? value : invalid() }

export async function getStaffTasks(): Promise<StaffTasks> {
  const value = record(await apiRequest('/staff/dashboard/tasks', { cache: 'no-store' }))
  return { pendingCodCount: integer(value.pendingCodCount), shipmentsToPackCount: integer(value.shipmentsToPackCount),
    shipmentsToHandoverCount: integer(value.shipmentsToHandoverCount), deliveryFailuresCount: integer(value.deliveryFailuresCount),
    lowStockSkuCount: integer(value.lowStockSkuCount), notificationsUnreadCount: integer(value.notificationsUnreadCount) }
}

export async function getRevenue(): Promise<RevenueReport> {
  const value = record(await apiRequest('/admin/reports/revenue', { cache: 'no-store' }))
  if (!Array.isArray(value.days) || !Array.isArray(value.paymentMethods)) return invalid()
  return { from: text(value.from), to: text(value.to), revenue: number(value.revenue), completedOrderCount: integer(value.completedOrderCount),
    averageOrderValue: number(value.averageOrderValue),
    days: value.days.map(raw => { const item = record(raw); return { date: text(item.date), revenue: number(item.revenue), completedOrderCount: integer(item.completedOrderCount) } }),
    paymentMethods: value.paymentMethods.map(raw => { const item = record(raw); return { paymentMethod: text(item.paymentMethod), revenue: number(item.revenue), completedOrderCount: integer(item.completedOrderCount) } }) }
}

export async function getLowStock(): Promise<LowStockReport> {
  const value = record(await apiRequest('/admin/reports/low-stock', { cache: 'no-store' }))
  if (!Array.isArray(value.items)) return invalid()
  return { threshold: integer(value.threshold), items: value.items.map(raw => { const item = record(raw); return {
    productSkuId: text(item.productSkuId), productTitle: text(item.productTitle), skuCode: text(item.skuCode), optionLabel: text(item.optionLabel),
    availableQty: integer(item.availableQty), physicalQty: integer(item.physicalQty), reservedQty: integer(item.reservedQty), allocatedQty: integer(item.allocatedQty),
  } }) }
}

export async function getAuditLogs(action = ''): Promise<AuditPage> {
  const query = new URLSearchParams({ page: '1', pageSize: '30' })
  if (action.trim()) query.set('action', action.trim())
  const value = record(await apiRequest(`/admin/audit-logs?${query}`, { cache: 'no-store' }))
  if (!Array.isArray(value.items)) return invalid()
  return { page: integer(value.page), pageSize: integer(value.pageSize), totalCount: integer(value.totalCount),
    items: value.items.map(raw => { const item = record(raw); return { id: text(item.id), actorId: nullableText(item.actorId), actorType: text(item.actorType),
      actorName: nullableText(item.actorName), action: text(item.action), entityType: text(item.entityType), entityId: text(item.entityId),
      beforeData: nullableText(item.beforeData), afterData: nullableText(item.afterData), correlationId: nullableText(item.correlationId), occurredAt: text(item.occurredAt) } }) }
}
