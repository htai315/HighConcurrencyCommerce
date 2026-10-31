import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type NotificationItem = {
  id: string
  type: string
  title: string
  body: string
  createdAt: string
  readAt: string | null
}

export type NotificationFeed = { items: NotificationItem[]; unreadCount: number }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function item(value: unknown): NotificationItem {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id), type: text(value.type), title: text(value.title), body: text(value.body),
    createdAt: text(value.createdAt), readAt: value.readAt === null ? null : text(value.readAt),
  }
}
function feed(value: unknown): NotificationFeed {
  if (!isRecord(value) || !Array.isArray(value.items)
    || typeof value.unreadCount !== 'number' || !Number.isSafeInteger(value.unreadCount) || value.unreadCount < 0) return invalid()
  return { items: value.items.map(item), unreadCount: value.unreadCount }
}

export async function notificationLoader(): Promise<NotificationFeed> {
  return feed(await apiRequest('/notifications?limit=50', { cache: 'no-store' }))
}

export async function markNotificationRead(id: string): Promise<NotificationItem> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return item(await apiRequest(`/notifications/${encodeURIComponent(id)}/read`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  }))
}
