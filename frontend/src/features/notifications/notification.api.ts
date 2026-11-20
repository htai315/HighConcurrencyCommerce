import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type NotificationItem = {
  id: string
  type: string
  title: string
  body: string
  createdAt: string
  readAt: string | null
  actionUrl: string | null
}

export type NotificationFeed = { items: NotificationItem[]; unreadCount: number; hasMore?: boolean }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function internalAction(value: unknown): string | null {
  if (value === null || value === undefined) return null
  const path = text(value)
  if (!path.startsWith('/') || path.startsWith('//') || /[\\\r\n]/.test(path)) return null
  return path
}
function item(value: unknown): NotificationItem {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id), type: text(value.type), title: text(value.title), body: text(value.body),
    createdAt: text(value.createdAt), readAt: value.readAt === null ? null : text(value.readAt),
    actionUrl: internalAction(value.actionUrl),
  }
}
function feed(value: unknown): NotificationFeed {
  if (!isRecord(value) || !Array.isArray(value.items)
    || typeof value.unreadCount !== 'number' || !Number.isSafeInteger(value.unreadCount) || value.unreadCount < 0) return invalid()
  return { items: value.items.map(item), unreadCount: value.unreadCount, hasMore: Boolean(value.hasMore) }
}

export async function fetchNotifications(options?: { unreadOnly?: boolean; limit?: number; before?: string; beforeId?: string }): Promise<NotificationFeed> {
  const params = new URLSearchParams()
  if (options?.unreadOnly) params.set('unreadOnly', 'true')
  if (options?.limit) params.set('limit', String(options.limit))
  if (options?.before) params.set('before', options.before)
  if (options?.beforeId) params.set('beforeId', options.beforeId)
  const qs = params.toString() ? `?${params.toString()}` : ''
  return feed(await apiRequest(`/notifications${qs}`, { cache: 'no-store' }))
}

export async function notificationLoader(): Promise<NotificationFeed> {
  return fetchNotifications({ limit: 50 })
}

export async function markNotificationRead(id: string): Promise<NotificationItem> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return item(await apiRequest(`/notifications/${encodeURIComponent(id)}/read`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  }))
}
