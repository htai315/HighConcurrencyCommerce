import { useCallback, useEffect, useState } from 'react'
import { formatPrice } from '../catalog/catalog.api'
import { deliverCodShipment, deliverPrepaidShipment, failDelivery, getFulfillmentQueue, handoverShipment, packShipment, retryDelivery, returnStock, startFulfillment } from './fulfillment.api'
import type { FulfillmentOrder } from './fulfillment.api'

export function FulfillmentPanel({ refreshKey }: { refreshKey?: number } = {}) {
  const [orders, setOrders] = useState<FulfillmentOrder[]>([])
  const [loading, setLoading] = useState(true)
  const [stage, setStage] = useState('ALL')
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [references, setReferences] = useState<Record<string, string>>({})
  const [failureReasons, setFailureReasons] = useState<Record<string, string>>({})
  const [returnEvidence, setReturnEvidence] = useState<Record<string, string>>({})

  const refresh = useCallback(() => {
    setLoading(true)
    getFulfillmentQueue()
      .then(data => setOrders(data))
      .catch(() => setNotice({ type: 'error', text: 'Chưa thể tải hàng đợi fulfillment.' }))
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    let ignore = false
    getFulfillmentQueue()
      .then(data => { if (!ignore) setOrders(data) })
      .catch(() => { if (!ignore) setNotice({ type: 'error', text: 'Chưa thể tải hàng đợi fulfillment.' }) })
      .finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [refreshKey])

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
      const prepaid = order.paymentStatus === 'SUCCESS'
      const reference = references[order.orderId] ?? ''
      const failureReason = failureReasons[order.orderId] ?? ''
      return <div className="delivery-confirmation">
        {prepaid ? <>
          <p className="alert success">Đơn đã thanh toán PayOS, không thu thêm tiền khi giao.</p>
          <button type="button" className="button-primary" disabled={busy === order.orderId}
            onClick={() => void run(order, () => deliverPrepaidShipment(order.shipmentId!), 'Giao thành công đơn đã thanh toán PayOS.')}>Xác nhận đã giao</button>
        </> : <>
          <label>Mã biên nhận thu COD
            <input value={reference} maxLength={100} placeholder="VD: COD-20261030-001"
              onChange={event => setReferences(current => ({ ...current, [order.orderId]: event.target.value }))} />
          </label>
          <button type="button" className="button-primary" disabled={busy === order.orderId || reference.trim().length < 3}
            onClick={() => void run(order, () => deliverCodShipment(order.shipmentId!, order.totalAmount, reference.trim()), 'Giao thành công, đã ghi nhận thu COD và hoàn tất đơn.')}>Xác nhận giao & thu COD</button>
        </>}
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
    <div className="cod-queue-header">
      <div>
        <h2>Fulfillment đơn hàng ({orders.length})</h2>
        <p>Xử lý đơn COD và đơn PayOS đã thanh toán: tiếp nhận → đóng gói → xuất kho → giao hàng.</p>
      </div>
      <button
        type="button"
        className="button-secondary"
        data-testid="fulfillment-refresh-button"
        onClick={() => refresh()}
        disabled={loading}
      >
        {loading ? 'Đang tải…' : 'Làm mới'}
      </button>
    </div>
    {notice && <p role="alert" className={`alert ${notice.type === 'success' ? 'success' : ''}`}>{notice.text}</p>}
    <label className="workspace-search">Lọc trạng thái giao hàng<select value={stage} onChange={event => setStage(event.target.value)}><option value="ALL">Tất cả</option><option value="NEW">Chờ tiếp nhận</option><option value="PENDING_PACKING">Chờ đóng gói</option><option value="PACKED">Chờ bàn giao</option><option value="SHIPPING">Đang giao</option><option value="DELIVERY_FAILED">Giao thất bại</option></select></label>
    {!loading && orders.length > 0 && !orders.some(order => stage === 'ALL' || (stage === 'NEW' ? !order.shipmentId : order.shipmentStatus === stage)) && <p role="status">Không có đơn ở trạng thái đã chọn.</p>}
    {loading ? <p role="status">Đang tải fulfillment…</p> : orders.length === 0
      ? <div className="empty-state compact"><h3>Không có đơn cần fulfillment</h3></div>
      : <div className="fulfillment-list">{orders.filter(order => stage === 'ALL' || (stage === 'NEW' ? !order.shipmentId : order.shipmentStatus === stage)).map(order => <article className="fulfillment-row" key={order.orderId} data-shipment-id={order.shipmentId ?? undefined}>
          <div><p className="eyebrow">{order.orderNumber}</p><h3>{order.customerName}</h3><span>{order.recipientName} · {order.shippingAddress}</span></div>
          <div><span>Shipment</span><strong data-testid="shipment-status">{order.shipmentStatus ?? 'CHƯA TẠO'}</strong>{order.shipmentId && <small className="shipment-id">ID: <code>{order.shipmentId}</code></small>}</div>
          <div><span>{order.paymentStatus === 'SUCCESS' ? 'Đã thanh toán PayOS' : 'COD phải thu'}</span><strong>{formatPrice(order.totalAmount)}</strong></div>
          <div className="fulfillment-action">{actionFor(order)}</div>
        </article>)}</div>}
  </div>
}
