import { useEffect, useState } from 'react'
import { formatPrice } from '../catalog/catalog.api'
import { deliverCodShipment, failDelivery, getFulfillmentQueue, handoverShipment, packShipment, retryDelivery, returnStock, startFulfillment } from './fulfillment.api'
import type { FulfillmentOrder } from './fulfillment.api'

export function FulfillmentPanel() {
  const [orders, setOrders] = useState<FulfillmentOrder[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [references, setReferences] = useState<Record<string, string>>({})
  const [failureReasons, setFailureReasons] = useState<Record<string, string>>({})
  const [returnEvidence, setReturnEvidence] = useState<Record<string, string>>({})

  useEffect(() => {
    let ignore = false
    getFulfillmentQueue()
      .then(data => { if (!ignore) setOrders(data) })
      .catch(() => { if (!ignore) setNotice({ type: 'error', text: 'Chưa thể tải hàng đợi fulfillment.' }) })
      .finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [])

  async function run(order: FulfillmentOrder, action: () => Promise<FulfillmentOrder>, success: string) {
    setBusy(order.orderId); setNotice(null)
    try {
      const updated = await action()
      if (updated.orderStatus === 'COMPLETED' || updated.orderStatus === 'CANCELLED') setOrders(current => current.filter(item => item.orderId !== order.orderId))
      else setOrders(current => current.map(item => item.orderId === order.orderId ? updated : item))
      setNotice({ type: 'success', text: success })
    } catch { setNotice({ type: 'error', text: 'Thao tác thất bại hoặc đã được Staff khác xử lý. Hãy làm mới hàng đợi.' }) }
    finally { setBusy(null) }
  }

  function actionFor(order: FulfillmentOrder) {
    if (!order.shipmentId) return <button type="button" className="button-primary" disabled={busy === order.orderId}
      onClick={() => void run(order, () => startFulfillment(order.orderId), 'Đã tiếp nhận đơn vào fulfillment.')}>Tiếp nhận đơn</button>
    if (order.shipmentStatus === 'PENDING_PACKING') return <button type="button" className="button-primary" disabled={busy === order.orderId}
      onClick={() => void run(order, () => packShipment(order.shipmentId!), 'Đã xác nhận đóng gói.')}>Xác nhận đóng gói</button>
    if (order.shipmentStatus === 'PACKED') return <button type="button" className="button-primary" disabled={busy === order.orderId}
      onClick={() => void run(order, () => handoverShipment(order.shipmentId!), 'Đã bàn giao vận chuyển và xuất kho.')}>Bàn giao & xuất kho</button>
    if (order.shipmentStatus === 'SHIPPING') {
      const reference = references[order.orderId] ?? ''
      const failureReason = failureReasons[order.orderId] ?? ''
      return <div className="delivery-confirmation">
        <label>Mã biên nhận thu COD
          <input value={reference} maxLength={100} placeholder="VD: COD-20261030-001"
            onChange={event => setReferences(current => ({ ...current, [order.orderId]: event.target.value }))} />
        </label>
        <button type="button" className="button-primary" disabled={busy === order.orderId || reference.trim().length < 3}
          onClick={() => void run(order, () => deliverCodShipment(order.shipmentId!, order.totalAmount, reference.trim()), 'Giao thành công, đã ghi nhận thu COD và hoàn tất đơn.')}>Xác nhận giao & thu COD</button>
        <span className="delivery-divider">hoặc</span>
        <label>Lý do giao thất bại
          <input value={failureReason} maxLength={500} placeholder="VD: Không liên hệ được khách"
            onChange={event => setFailureReasons(current => ({ ...current, [order.orderId]: event.target.value }))} />
        </label>
        <button type="button" className="button-secondary" disabled={busy === order.orderId || failureReason.trim().length < 3}
          onClick={() => void run(order, () => failDelivery(order.shipmentId!, failureReason.trim()), 'Đã ghi nhận lần giao thất bại.')}>Ghi nhận giao thất bại</button>
      </div>
    }
    if (order.shipmentStatus === 'DELIVERY_FAILED') {
      const evidence = returnEvidence[order.orderId] ?? ''
      return <div className="delivery-confirmation">
        <strong>Đã giao lại {order.redeliveryCount}/2 lần</strong>
        {order.redeliveryCount < 2 && <button type="button" className="button-primary" disabled={busy === order.orderId}
          onClick={() => void run(order, () => retryDelivery(order.shipmentId!), `Đã bắt đầu lần giao lại ${order.redeliveryCount + 1}/2.`)}>Giao lại</button>}
        <label>Bằng chứng hàng đã về kho
          <input value={evidence} maxLength={500} placeholder="VD: Biên bản nhận lại BB-001"
            onChange={event => setReturnEvidence(current => ({ ...current, [order.orderId]: event.target.value }))} />
        </label>
        <button type="button" className="button-secondary" disabled={busy === order.orderId || evidence.trim().length < 3}
          onClick={() => void run(order, () => returnStock(order.shipmentId!, evidence.trim()), 'Đã hoàn kho đúng một lần và đóng đơn.')}>Xác nhận hàng về & hoàn kho</button>
      </div>
    }
    return null
  }

  return <div className="management-card fulfillment-panel">
    <div className="cod-queue-header"><div><h2>Fulfillment COD ({orders.length})</h2>
      <p>Tiếp nhận → đóng gói → xuất kho → giao/thu COD hoặc retry/hoàn kho.</p></div></div>
    {notice && <p role="alert" className={`alert ${notice.type === 'success' ? 'success' : ''}`}>{notice.text}</p>}
    {loading ? <p role="status">Đang tải fulfillment…</p> : orders.length === 0
      ? <div className="empty-state compact"><h3>Không có đơn cần fulfillment</h3></div>
      : <div className="fulfillment-list">{orders.map(order => <article className="fulfillment-row" key={order.orderId}>
          <div><p className="eyebrow">{order.orderNumber}</p><h3>{order.customerName}</h3><span>{order.recipientName} · {order.shippingAddress}</span></div>
          <div><span>Shipment</span><strong>{order.shipmentStatus ?? 'CHƯA TẠO'}</strong></div>
          <div><span>COD phải thu</span><strong>{formatPrice(order.totalAmount)}</strong></div>
          <div className="fulfillment-action">{actionFor(order)}</div>
        </article>)}</div>}
  </div>
}
