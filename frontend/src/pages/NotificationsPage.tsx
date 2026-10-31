import { useState } from 'react'
import { useLoaderData } from 'react-router-dom'
import { markNotificationRead, notificationLoader } from '../features/notifications/notification.api'
import type { NotificationItem } from '../features/notifications/notification.api'
import { PageHeading } from '../components/layout/PageHeading'

function getNotificationIcon(type: string): string {
  if (type.includes('SHIPMENT') || type.includes('DELIVERY')) return '🚚'
  if (type.includes('ORDER') || type.includes('COD')) return '📦'
  if (type.includes('PAYMENT')) return '💳'
  if (type.includes('FLASH')) return '⚡'
  return '🔔'
}

export function NotificationsPage() {
  const initial = useLoaderData<typeof notificationLoader>()
  const [items, setItems] = useState(initial.items)
  const [unreadCount, setUnreadCount] = useState(initial.unreadCount)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function read(notification: NotificationItem) {
    if (notification.readAt) return
    setBusy(notification.id); setError(null)
    try {
      const updated = await markNotificationRead(notification.id)
      setItems(current => current.map(item => item.id === updated.id ? updated : item))
      setUnreadCount(current => Math.max(0, current - 1))
    } catch { setError('Chưa thể đánh dấu thông báo đã đọc. Vui lòng thử lại.') }
    finally { setBusy(null) }
  }

  return (
    <section className="content-page notification-page">
      <PageHeading title="Thông báo của bạn" description="Mọi cập nhật về đơn hàng, tập trung tại một nơi." extra={<span className="notification-count">{unreadCount} chưa đọc</span>} />

      {error && <p role="alert" className="alert error">{error}</p>}

      {items.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon" aria-hidden="true">🔔</div>
          <h2>Chưa có thông báo</h2>
          <p>Cập nhật đơn hàng mới sẽ xuất hiện tại đây.</p>
        </div>
      ) : (
        <div className="notification-list">
          {items.map(notification => (
            <article
              key={notification.id}
              className={`notification-card${notification.readAt ? '' : ' unread'}`}
            >
              <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
                <span style={{ fontSize: 24, padding: '6px', background: 'var(--paper-deep)', borderRadius: 10 }}>
                  {getNotificationIcon(notification.type)}
                </span>
                <div>
                  <span className="notification-type">{notification.readAt ? 'Cập nhật đơn hàng' : 'Thông báo mới'}</span>
                  <h2 style={{ fontSize: 16, margin: '4px 0 6px' }}>{notification.title}</h2>
                  <p style={{ margin: '0 0 6px', color: 'var(--ink-secondary)', fontSize: 14 }}>{notification.body}</p>
                  <time dateTime={notification.createdAt} style={{ fontSize: 12, color: 'var(--muted)' }}>
                    {new Date(notification.createdAt).toLocaleString('vi-VN')}
                  </time>
                </div>
              </div>

              {notification.readAt ? (
                <span className="read-state">Đã đọc</span>
              ) : (
                <button
                  type="button"
                  className="button-secondary"
                  disabled={busy === notification.id}
                  onClick={() => void read(notification)}
                  style={{ minHeight: 36, padding: '0 14px', fontSize: 13 }}
                >
                  Đánh dấu đã đọc
                </button>
              )}
            </article>
          ))}
        </div>
      )}
    </section>
  )
}
