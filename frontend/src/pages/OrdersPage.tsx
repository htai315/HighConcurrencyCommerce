import { useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { PageHeading } from '../components/layout/PageHeading'
import { ConfirmDialog } from '../components/layout/ConfirmDialog'
import { formatPrice } from '../features/catalog/catalog.api'
import { cancelCustomerOrder, getCustomerOrder, ordersLoader } from '../features/orders/orders.api'
import type { CustomerOrder, CustomerOrderPage } from '../features/orders/orders.api'
import { cancelPayOsPayment, createPayOsAttempt, redirectToPayOs, rememberPayOsReturnOrder } from '../features/payments/payos.api'
import { CreateReturnModal } from '../features/returns/CreateReturnModal'
import { CustomerTrackingPanel } from '../features/carrier-tracking/CustomerTrackingPanel'

const stateLabels: Record<string, string> = {
  PENDING_COD_CONFIRMATION: 'Chờ xác nhận COD', CONFIRMED: 'Đã xác nhận', FULFILLING: 'Đang xử lý',
  PENDING_PAYMENT: 'Chờ thanh toán PayOS', PENDING: 'Đang chờ thanh toán', EXPIRED: 'Đã hết hạn',
  COMPLETED: 'Hoàn tất', CANCELLED: 'Đã hủy', COD_PENDING: 'Chờ thu COD', VOID: 'Đã vô hiệu',
  UNPAID: 'Chưa thanh toán', PAID: 'Đã thanh toán', HELD: 'Đang giữ hàng', RELEASED: 'Đã hoàn kho',
  ALLOCATED: 'Đã phân bổ', PACKING: 'Đang đóng gói', SHIPPING: 'Đang giao', DELIVERED: 'Đã giao',
  DELIVERY_FAILED: 'Giao thất bại', SUCCESS: 'Thành công', FAILED: 'Thất bại',
  DRAFT: 'Đang tạo vận đơn', LABEL_READY: 'Đã có nhãn', HANDED_OVER: 'Đã bàn giao hãng', IN_TRANSIT: 'Đang trung chuyển',
  OUT_FOR_DELIVERY: 'Đang giao đến bạn', RETURNING: 'Đang hoàn hàng', RETURNED: 'Đã hoàn hàng',
  CUSTOMER_CANCELLED: 'Khách hàng hủy', LATE_PAID: 'Thanh toán sau khi đơn đã đóng',
}

function label(value: string | null): string { return value ? (stateLabels[value] ?? value.replaceAll('_', ' ')) : 'Chưa tạo' }
function date(value: string | null): string { return value ? new Date(value).toLocaleString('vi-VN') : '—' }

function OrderCard({ order, onCancelled }: { order: CustomerOrder; onCancelled: (order: CustomerOrder) => void }) {
  const [expanded, setExpanded] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [showReturnModal, setShowReturnModal] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  async function cancel() {
    const normalized = reason.trim()
    if (normalized.length < 3) {
      setMessage({ kind: 'error', text: 'Lý do hủy cần ít nhất 3 ký tự.' })
      return
    }
    setBusy(true); setMessage(null)
    try {
      const updated = order.paymentMethod === 'PAYOS'
        ? await cancelPayOsPayment(order.id, normalized).then(() => getCustomerOrder(order.id))
        : await cancelCustomerOrder(order.id, normalized)
      onCancelled(updated)
      setMessage({ kind: 'success', text: 'Đơn đã được hủy và hàng giữ đã được hoàn lại kho.' })
    } catch {
      setMessage({ kind: 'error', text: 'Không thể hủy đơn ở trạng thái hiện tại. Vui lòng tải lại trang.' })
    } finally { setBusy(false); setConfirming(false) }
  }

  async function resumePayOs() {
    setBusy(true); setMessage(null)
    try {
      const attempt = await createPayOsAttempt(order.id)
      if (!attempt.checkoutUrl) throw new Error('PayOS checkout URL missing')
      rememberPayOsReturnOrder(order.id)
      redirectToPayOs(attempt.checkoutUrl)
    } catch {
      setMessage({ kind: 'error', text: 'Chưa thể mở cổng PayOS. Đơn và tồn kho vẫn được giữ; bạn có thể thử lại.' })
      setBusy(false)
    }
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

      <ol className="order-timeline" aria-label="Các mốc đã ghi nhận">
        {([
          ['Đặt hàng', order.createdAt], ['Xác nhận', order.confirmedAt],
          ['Gửi hàng', order.shippedAt], ['Giao thành công', order.deliveredAt],
        ] as const).map(([title, timestamp]) => <li key={title} className={timestamp ? 'recorded' : ''}>
          <span className="timeline-marker" aria-hidden="true">{timestamp ? '✓' : '·'}</span>
          <strong>{title}</strong><span>{timestamp ? date(timestamp) : order.orderStatus === 'CANCELLED' ? 'Đơn đã hủy' : 'Chưa ghi nhận'}</span>
        </li>)}
      </ol>

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

      {order.tracking && <CustomerTrackingPanel orderId={order.id} summary={order.tracking} />}

      <ul className="order-item-summary">
        {order.items.map(item => (
          <li key={item.productSkuId}>
            <div>
              <strong>{item.productTitle}</strong>
              <span style={{ fontSize: 13, color: 'var(--muted)', display: 'block' }}>{item.optionLabel} · SKU: {item.skuCode}</span>
            </div>
            <div style={{ textAlign: 'right' }}>
              <span style={{ fontSize: 14 }}>{item.quantity} × {formatPrice(item.unitPrice)}</span>
              {order.orderStatus === 'COMPLETED' && order.shipmentStatus === 'DELIVERED' && item.productId && (
                <div style={{ marginTop: 6 }}>
                  <Link
                    to={`/products/${item.productId}#reviews`}
                    className="button secondary write-review-order-link"
                    style={{ padding: '3px 8px', fontSize: 12, display: 'inline-block' }}
                  >
                    ★ Đánh giá sản phẩm
                  </Link>
                </div>
              )}
            </div>
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
            <div><dt>Phương thức giao</dt><dd>{order.shippingMethodName}{order.shippingEstimatedMinDays != null && order.shippingEstimatedMaxDays != null ? ` · dự kiến ${order.shippingEstimatedMinDays}–${order.shippingEstimatedMaxDays} ngày` : ''}</dd></div>
            <div><dt>Tạm tính hàng</dt><dd>{formatPrice(order.subtotalAmount)}</dd></div>
            {order.voucherCode && (
              <>
                <div><dt>Mã giảm giá</dt><dd style={{ color: 'var(--success-ink, #16a34a)' }}>{order.voucherCode} (-{formatPrice(order.voucherDiscountAmount)})</dd></div>
              </>
            )}
            <div><dt>Phí vận chuyển</dt><dd>{order.shippingFee === 0 ? 'Miễn phí' : formatPrice(order.shippingFee)}</dd></div>
            <div><dt>Tổng thanh toán</dt><dd><strong>{formatPrice(order.totalAmount)}</strong></dd></div>
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
        <details className="order-cancel-box">
          <summary>Yêu cầu hủy đơn</summary>
          <label htmlFor={`cancel-${order.id}`}>Lý do hủy {order.paymentMethod === 'PAYOS' ? 'thanh toán PayOS' : 'đơn COD'}</label>
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
              onClick={() => {
                if (reason.trim().length < 3) { setMessage({ kind: 'error', text: 'Lý do hủy cần ít nhất 3 ký tự.' }); return }
                setConfirming(true)
              }}
              style={{ background: '#ffffff', color: 'var(--warning-ink)', borderColor: '#fcd34d' }}
            >
              {busy ? 'Đang hủy…' : 'Hủy đơn'}
            </button>
          </div>
          <small style={{ color: 'var(--warning-ink)', opacity: 0.9 }}>Bạn có thể hủy khi cửa hàng chưa xác nhận đơn.</small>
        </details>
      )}
      {order.paymentMethod === 'PAYOS' && order.orderStatus === 'PENDING_PAYMENT' && order.paymentStatus === 'PENDING' && (
        <div className="order-payos-actions">
          <button type="button" className="button-primary" disabled={busy} onClick={() => void resumePayOs()}>
            {busy ? 'Đang mở PayOS…' : 'Tiếp tục thanh toán PayOS'}
          </button>
          <small>Đơn chỉ được xử lý sau khi backend xác minh thanh toán thành công từ PayOS.</small>
        </div>
      )}
      {order.orderStatus === 'COMPLETED' && order.paymentMethod === 'COD' && order.shipmentStatus === 'DELIVERED' && (
        <div style={{ marginTop: 12 }}>
          <button
            type="button"
            className="button secondary"
            style={{ fontSize: 13, padding: '5px 12px' }}
            onClick={() => setShowReturnModal(true)}
          >
            ↺ Yêu cầu đổi / trả hàng
          </button>
        </div>
      )}

      {showReturnModal && (
        <CreateReturnModal
          orderId={order.id}
          orderNumber={order.orderNumber}
          onClose={() => setShowReturnModal(false)}
          onCreated={() => {
            setMessage({
              kind: 'success',
              text: 'Yêu cầu đổi/trả đã được gửi thành công. Bạn có thể theo dõi trong mục Đổi trả.',
            })
          }}
        />
      )}

      {confirming && <ConfirmDialog title={`Hủy đơn ${order.orderNumber}?`} description={`Lý do: ${reason.trim()}. Sau khi hủy thành công, bạn cần đặt đơn mới nếu muốn mua lại.`} confirmLabel={order.paymentMethod === 'PAYOS' ? 'Hủy thanh toán' : 'Xác nhận hủy đơn'} busy={busy} onCancel={() => setConfirming(false)} onConfirm={() => void cancel()} />}
      {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`alert order-message ${message.kind}`} style={{ marginTop: 14 }}>{message.text}</p>}
    </article>
  )
}

export function OrdersPage() {
  const initial = useLoaderData<typeof ordersLoader>()
  return <OrdersView key={`${initial.page}:${initial.items.map(order => `${order.id}:${order.orderStatus}`).join(',')}`} initial={initial} />
}

function OrdersView({ initial }: { initial: CustomerOrderPage }) {
  const [orders, setOrders] = useState(initial.items)
  const pages = Math.ceil(initial.totalCount / initial.pageSize)

  function replace(updated: CustomerOrder) {
    setOrders(current => current.map(order => order.id === updated.id ? updated : order))
  }

  return (
    <section className="content-page customer-orders-page">
      <PageHeading title="Đơn hàng của tôi" description="Xem lại sản phẩm đã đặt và theo dõi các mốc giao hàng được cửa hàng cập nhật." extra={<span className="notification-count">{initial.totalCount} đơn</span>} />
      {orders.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon" aria-hidden="true">📦</div>
          <h2>{initial.page > 1 ? 'Trang này không có đơn hàng' : 'Bạn chưa có đơn hàng'}</h2>
          <p>{initial.page > 1 ? 'Quay về trang đầu để xem các đơn đã đặt.' : 'Sản phẩm cầu lông phù hợp đang chờ bạn khám phá.'}</p>
          <Link to={initial.page > 1 ? '/orders' : '/'} className="button-primary">{initial.page > 1 ? 'Về trang đầu' : 'Khám phá cửa hàng'}</Link>
        </div>
      ) : (
        <div className="customer-order-list">
          {orders.map(order => (
            <OrderCard key={order.id} order={order} onCancelled={replace} />
          ))}
        </div>
      )}
      {(pages > 1 || initial.page > 1) && <nav className="data-pagination" aria-label="Phân trang đơn hàng">
        {initial.page > 1 && <Link className="button-secondary" to={`/orders?page=${initial.page - 1}`}>Trang trước</Link>}
        <span>Trang {initial.page}{pages >= initial.page ? ` / ${pages}` : ''}</span>
        {initial.page < pages && <Link className="button-secondary" to={`/orders?page=${initial.page + 1}`}>Trang sau</Link>}
      </nav>}
    </section>
  )
}
