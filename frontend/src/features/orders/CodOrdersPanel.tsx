import { useCallback, useEffect, useState } from 'react'
import { formatPrice } from '../catalog/catalog.api'
import { confirmCodOrder, getPendingCodOrders } from './cod-orders.api'
import type { CodOrder } from './cod-orders.api'

export function CodOrdersPanel() {
  const [orders, setOrders] = useState<CodOrder[]>([])
  const [loading, setLoading] = useState(true)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try { setOrders(await getPendingCodOrders()) }
    catch { setNotice({ type: 'error', text: 'Chưa thể tải hàng đợi COD.' }) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => {
    let ignore = false
    getPendingCodOrders()
      .then(data => { if (!ignore) setOrders(data) })
      .catch(() => { if (!ignore) setNotice({ type: 'error', text: 'Chưa thể tải hàng đợi COD.' }) })
      .finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [])

  async function confirm(order: CodOrder) {
    setConfirming(order.orderId); setNotice(null)
    try {
      await confirmCodOrder(order.orderId)
      setOrders(current => current.filter(item => item.orderId !== order.orderId))
      setNotice({ type: 'success', text: `Đã xác nhận ${order.orderNumber}; tồn giữ đã chuyển sang phân bổ.` })
    } catch {
      setNotice({ type: 'error', text: 'Không thể xác nhận. Đơn có thể đã hết hạn hoặc được xử lý bởi Staff khác.' })
      await refresh()
    } finally { setConfirming(null) }
  }

  return <div className="management-card">
    <div className="cod-queue-header">
      <div>
        <h2>Đơn COD chờ xác nhận ({orders.length})</h2>
        <p>Staff phải xác nhận trong 24 giờ. Xác nhận chuyển tồn Reserved sang Allocated; Worker tự hết hạn và nhả tồn.</p>
      </div>
      <button type="button" className="button-secondary" onClick={() => void refresh()} disabled={loading}>
        {loading ? 'Đang tải…' : 'Làm mới'}
      </button>
    </div>
    {notice && <p role="alert" className={`alert ${notice.type === 'success' ? 'success' : ''}`}>{notice.text}</p>}
    {loading && orders.length === 0 ? <p role="status">Đang tải hàng đợi COD…</p> : orders.length === 0
      ? <div className="empty-state compact"><h3>Không có đơn COD chờ xác nhận</h3><p>Hàng đợi hiện đã được xử lý hết.</p></div>
      : <div className="cod-order-grid">{orders.map(order => <article className="cod-order-card" key={order.orderId}>
          <div className="cod-order-title">
            <div><p className="eyebrow">{order.orderNumber}</p><h3>{order.customerName}</h3><span>{order.customerEmail}</span></div>
            <span className="badge badge-pending">Chờ xác nhận</span>
          </div>
          <dl>
            <div><dt>Người nhận</dt><dd>{order.recipientName} · {order.recipientPhone}</dd></div>
            <div><dt>Giao đến</dt><dd>{order.shippingAddress}</dd></div>
            <div><dt>Thời hạn</dt><dd>{new Date(order.deadlineAt).toLocaleString('vi-VN')}</dd></div>
          </dl>
          <ul>{order.items.map(item => <li key={item.productSkuId}>
            <span>{item.productTitle} · {item.optionLabel}</span><strong>{item.quantity} × {formatPrice(item.unitPrice)}</strong>
          </li>)}</ul>
          <div className="cod-order-footer"><strong>{formatPrice(order.totalAmount)}</strong>
            <button type="button" className="button-primary" disabled={confirming === order.orderId}
              onClick={() => void confirm(order)}>{confirming === order.orderId ? 'Đang xác nhận…' : 'Xác nhận đơn COD'}</button>
          </div>
        </article>)}</div>}
  </div>
}
