import { useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { PageHeading } from '../components/layout/PageHeading'
import { formatPrice } from '../features/catalog/catalog.api'
import { cancelCustomerOrder, ordersLoader } from '../features/orders/orders.api'
import type { CustomerOrder } from '../features/orders/orders.api'

const stateLabels: Record<string, string> = {
  PENDING_COD_CONFIRMATION: 'Chờ xác nhận COD', CONFIRMED: 'Đã xác nhận', FULFILLING: 'Đang xử lý',
  COMPLETED: 'Hoàn tất', CANCELLED: 'Đã hủy', COD_PENDING: 'Chờ thu COD', VOID: 'Đã vô hiệu',
  UNPAID: 'Chưa thanh toán', PAID: 'Đã thanh toán', HELD: 'Đang giữ hàng', RELEASED: 'Đã hoàn kho',
  ALLOCATED: 'Đã phân bổ', PACKING: 'Đang đóng gói', SHIPPING: 'Đang giao', DELIVERED: 'Đã giao',
  DELIVERY_FAILED: 'Giao thất bại', SUCCESS: 'Thành công', FAILED: 'Thất bại',
  CUSTOMER_CANCELLED: 'Khách hàng hủy',
}

function label(value: string | null): string { return value ? (stateLabels[value] ?? value.replaceAll('_', ' ')) : 'Chưa tạo' }
function date(value: string | null): string { return value ? new Date(value).toLocaleString('vi-VN') : '—' }

function OrderCard({ order, onCancelled }: { order: CustomerOrder; onCancelled: (order: CustomerOrder) => void }) {
  const [expanded, setExpanded] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  async function cancel() {
    const normalized = reason.trim()
    if (normalized.length < 3) {
      setMessage({ kind: 'error', text: 'Lý do hủy cần ít nhất 3 ký tự.' })
      return
    }
    setBusy(true); setMessage(null)
    try {
      const updated = await cancelCustomerOrder(order.id, normalized)
      onCancelled(updated)
      setMessage({ kind: 'success', text: 'Đơn đã được hủy và hàng giữ đã được hoàn lại kho.' })
    } catch {
      setMessage({ kind: 'error', text: 'Không thể hủy đơn ở trạng thái hiện tại. Vui lòng tải lại trang.' })
    } finally { setBusy(false) }
  }

  return (
    <article className="customer-order-card">
      <header className="customer-order-header">
        <div>
          <span className="order-number">{order.orderNumber}</span>
          <time dateTime={order.createdAt}>{date(order.createdAt)}</time>
        </div>
        <div className="order-head-right">
          <span className={`badge badge-${order.orderStatus.toLowerCase()}`}>{label(order.orderStatus)}</span>
          <strong style={{ fontSize: 18, color: 'var(--brand)' }}>{formatPrice(order.totalAmount)}</strong>
        </div>
      </header>

      <div className="order-state-grid" aria-label="Trạng thái đơn hàng">
        <div>
          <span>Đơn hàng</span>
          <strong>{label(order.orderStatus)}</strong>
        </div>
        <div>
          <span>Thanh toán · {order.paymentMethod}</span>
          <strong>{label(order.paymentStatus)}</strong>
        </div>
        <div>
          <span>Giữ hàng</span>
          <strong>{label(order.reservationStatus)}</strong>
        </div>
        <div>
          <span>Vận chuyển</span>
          <strong>{label(order.shipmentStatus)}</strong>
        </div>
      </div>

      <ul className="order-item-summary">
        {order.items.map(item => (
          <li key={item.productSkuId}>
            <div>
              <strong>{item.productTitle}</strong>
              <span style={{ fontSize: 13, color: 'var(--muted)', display: 'block' }}>{item.optionLabel} · SKU: {item.skuCode}</span>
            </div>
            <span style={{ fontSize: 14 }}>{item.quantity} × {formatPrice(item.unitPrice)}</span>
          </li>
        ))}
      </ul>

      <button
        type="button"
        className="text-button order-detail-toggle"
        aria-expanded={expanded}
        onClick={() => setExpanded(value => !value)}
        style={{ marginTop: 8 }}
      >
        {expanded ? 'Thu gọn chi tiết' : 'Xem chi tiết đơn'}
      </button>

      {expanded && (
        <div className="order-detail-panel">
          <dl>
            <div><dt>Người nhận</dt><dd>{order.recipientName} · {order.recipientPhone}</dd></div>
            <div><dt>Địa chỉ giao</dt><dd>{order.shippingAddress}</dd></div>
            <div><dt>Hạn xác nhận</dt><dd>{date(order.deadlineAt)}</dd></div>
            <div><dt>Xác nhận lúc</dt><dd>{date(order.confirmedAt)}</dd></div>
            <div><dt>Gửi hàng lúc</dt><dd>{date(order.shippedAt)}</dd></div>
            <div><dt>Giao thành công</dt><dd>{date(order.deliveredAt)}</dd></div>
            {order.closedReason && <div><dt>Lý do đóng</dt><dd>{label(order.closedReason)}</dd></div>}
            {order.cancellationReason && <div><dt>Lý do hủy</dt><dd>{order.cancellationReason}</dd></div>}
          </dl>
          {order.deliveryAttempts.length > 0 && (
            <div className="delivery-attempts">
              <h3 style={{ fontSize: 14, margin: '0 0 10px', fontWeight: 700 }}>Lịch sử giao hàng</h3>
              <ol style={{ paddingLeft: 20, margin: 0 }}>
                {order.deliveryAttempts.map(attempt => (
                  <li key={attempt.attemptNo} style={{ marginBottom: 6 }}>
                    <strong>Lần {attempt.attemptNo}: {label(attempt.result)}</strong>
                    <span style={{ display: 'block', fontSize: 12, color: 'var(--muted)' }}>
                      {date(attempt.finishedAt)}{attempt.note ? ` · ${attempt.note}` : ''}
                    </span>
                  </li>
                ))}
              </ol>
            </div>
          )}
        </div>
      )}

      {order.canCancel && (
        <div className="order-cancel-box">
          <label htmlFor={`cancel-${order.id}`}>Lý do hủy đơn COD</label>
          <div>
            <input
              id={`cancel-${order.id}`}
              value={reason}
              maxLength={500}
              onChange={event => setReason(event.target.value)}
              placeholder="Ví dụ: Tôi đặt nhầm sản phẩm"
            />
            <button
              type="button"
              className="button-secondary"
              disabled={busy}
              onClick={() => void cancel()}
              style={{ background: '#ffffff', color: 'var(--warning-ink)', borderColor: '#fcd34d' }}
            >
              {busy ? 'Đang hủy…' : 'Hủy đơn'}
            </button>
          </div>
          <small style={{ color: 'var(--warning-ink)', opacity: 0.9 }}>Chỉ hủy trực tiếp khi đơn COD còn chờ Staff xác nhận.</small>
        </div>
      )}
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`alert order-message ${message.kind}`} style={{ marginTop: 14 }}>{message.text}</p>}
    </article>
  )
}

export function OrdersPage() {
  const initial = useLoaderData<typeof ordersLoader>()
  const [orders, setOrders] = useState(initial.items)

  function replace(updated: CustomerOrder) {
    setOrders(current => current.map(order => order.id === updated.id ? updated : order))
  }

  return (
    <section className="content-page customer-orders-page">
      <PageHeading title="Theo dõi từng chặng giao hàng" description="Xem lại sản phẩm đã đặt, thông tin thanh toán và hành trình giao hàng của bạn." extra={<span className="notification-count">{initial.totalCount} đơn</span>} />
      {orders.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon" aria-hidden="true">📦</div>
          <h2>Bạn chưa có đơn hàng</h2>
          <p>Sản phẩm cầu lông phù hợp đang chờ bạn khám phá.</p>
          <Link to="/" className="button-primary">Khám phá cửa hàng</Link>
        </div>
      ) : (
        <div className="customer-order-list">
          {orders.map(order => (
            <OrderCard key={order.id} order={order} onCancelled={replace} />
          ))}
        </div>
      )}
    </section>
  )
}
