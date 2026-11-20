import { useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { fetchNotifications, markNotificationRead, notificationLoader } from '../features/notifications/notification.api'
import type { NotificationItem } from '../features/notifications/notification.api'
import { PageHeading } from '../components/layout/PageHeading'

function getNotificationIcon(type: string) {
  return <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">{type.startsWith('WISHLIST_') ? <><path d="M12 21s-8-4.8-8-11a4.5 4.5 0 0 1 8-2.8A4.5 4.5 0 0 1 20 10c0 6.2-8 11-8 11Z"/><path d="M12 8v6m-3-3h6"/></> : type.includes('PAYMENT') ? <><rect x="3" y="5" width="18" height="14" rx="3"/><path d="M3 10h18M7 15h4"/></> : <><path d="M5 17h14l-2-3V9a5 5 0 0 0-10 0v5ZM10 21h4"/><path d="M12 2v2"/></>}</svg>
}

function notificationLabel(type: string, unread: boolean) {
  if (type === 'WISHLIST_BACK_IN_STOCK') return 'Có hàng trở lại'
  if (type === 'WISHLIST_PRICE_DROP') return 'Giảm giá sản phẩm yêu thích'
  return unread ? 'Thông báo mới' : 'Cập nhật'
}

export function NotificationsPage() {
  const initial = useLoaderData<typeof notificationLoader>()
  const [items, setItems] = useState(initial.items)
  const [unreadCount, setUnreadCount] = useState(initial.unreadCount)
  const [hasMore, setHasMore] = useState(Boolean(initial.hasMore))
  const [loadingMore, setLoadingMore] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [unreadOnly, setUnreadOnly] = useState(false)

  async function toggleUnreadOnly(checked: boolean) {
    setUnreadOnly(checked)
    setError(null)
    try {
      const feed = await fetchNotifications({ unreadOnly: checked, limit: 50 })
      setItems(feed.items)
      setUnreadCount(feed.unreadCount)
      setHasMore(Boolean(feed.hasMore))
    } catch {
      setError('Chưa thể tải danh sách thông báo. Vui lòng thử lại.')
    }
  }

  async function loadMore() {
    if (loadingMore || items.length === 0) return
    const lastItem = items[items.length - 1]
    if (!lastItem) return
    setLoadingMore(true)
    setError(null)
    try {
      const feed = await fetchNotifications({
        unreadOnly,
        limit: 50,
        before: lastItem.createdAt,
        beforeId: lastItem.id,
      })
      setItems(current => [...current, ...feed.items])
      setUnreadCount(feed.unreadCount)
      setHasMore(Boolean(feed.hasMore))
    } catch {
      setError('Chưa thể tải thêm thông báo. Vui lòng thử lại.')
    } finally {
      setLoadingMore(false)
    }
  }

  async function read(notification: NotificationItem) {
    if (notification.readAt) return
    setBusy(notification.id); setError(null)
    try {
      const updated = await markNotificationRead(notification.id)
      setItems(current => unreadOnly
        ? current.filter(item => item.id !== updated.id)
        : current.map(item => item.id === updated.id ? updated : item))
      setUnreadCount(current => Math.max(0, current - 1))
    } catch { setError('Chưa thể đánh dấu thông báo đã đọc. Vui lòng thử lại.') }
    finally { setBusy(null) }
  }

  return (
    <section className="content-page notification-page">
      <PageHeading title="Thông báo của bạn" description="Mọi cập nhật về đơn hàng, tập trung tại một nơi." extra={<span className="notification-count">{unreadCount} chưa đọc</span>} />
      <label className="notification-filter"><input type="checkbox" checked={unreadOnly} onChange={event => void toggleUnreadOnly(event.target.checked)} />Chỉ hiện thông báo chưa đọc trong danh sách</label>
      {unreadOnly && items.length > 0 && !items.some(item => !item.readAt) && <p role="status">Bạn đã đọc hết thông báo trong danh sách này.</p>}

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
                  <span className="notification-type">{notificationLabel(notification.type, !notification.readAt)}</span>
                  <h2 style={{ fontSize: 16, margin: '4px 0 6px' }}>{notification.title}</h2>
                  <p style={{ margin: '0 0 6px', color: 'var(--ink-secondary)', fontSize: 14 }}>{notification.body}</p>
                  <time dateTime={notification.createdAt} style={{ fontSize: 12, color: 'var(--muted)' }}>
                    {new Date(notification.createdAt).toLocaleString('vi-VN')}
                  </time>
                  {notification.actionUrl && (
                    <Link className="notification-action-link" to={notification.actionUrl}>Xem sản phẩm <span aria-hidden="true">→</span></Link>
                  )}
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

      {hasMore && (
        <div style={{ textAlign: 'center', marginTop: 16 }}>
          <button
            type="button"
            className="button-secondary"
            disabled={loadingMore}
            onClick={() => void loadMore()}
          >
            {loadingMore ? 'Đang tải…' : 'Tải thêm thông báo'}
          </button>
        </div>
      )}
    </section>
  )
}
