import { useCallback, useEffect, useState } from 'react'
import {
  getExchangeShipments, handoverExchangeShipment, packExchangeShipment, recordExchangeDelivery,
  retryExchangeDelivery, returnExchangeStock, type ExchangeFulfillment,
} from './returns.api'

export function ExchangeFulfillmentPanel() {
  const [items, setItems] = useState<ExchangeFulfillment[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const load = useCallback(async () => setItems(await getExchangeShipments()), [])
  useEffect(() => {
    const timer = window.setTimeout(() => void load().catch(error => setMessage(error instanceof Error ? error.message : 'Không thể tải hàng đợi đổi hàng.')), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  async function run(id: string, action: () => Promise<ExchangeFulfillment>) {
    setBusy(id); setMessage(null)
    try { await action(); await load() } catch (error) { setMessage(error instanceof Error ? error.message : 'Thao tác thất bại.') } finally { setBusy(null) }
  }

  return <section className="exchange-fulfillment-panel" aria-labelledby="exchange-fulfillment-title">
    <div className="panel-heading-row">
      <div><span className="panel-kicker">T42 · Đổi trực tiếp</span><h2 id="exchange-fulfillment-title">Giao sản phẩm thay thế</h2></div>
      <button className="button secondary" type="button" onClick={() => void load()}>Làm mới</button>
    </div>
    {message && <div className="alert error" role="alert">{message}</div>}
    {items.length === 0 ? <div className="empty-state"><strong>Chưa có kiện đổi đang xử lý</strong><p>Kiện mới xuất hiện sau khi hàng khách trả đã được kiểm định.</p></div> :
      <div className="management-card-grid">{items.map(item => <article className="management-card" key={item.shipmentId}>
        <div className="management-card__header"><div><strong>Đơn {item.orderNumber}</strong><small>{item.lines.map(line => `${line.optionLabel} × ${line.quantity}`).join(' · ')}</small></div><span className="status-badge">{item.shipmentStatus}</span></div>
        <div className="management-card__actions">
          {item.shipmentStatus === 'PENDING_PACKING' && <button className="button primary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => packExchangeShipment(item.shipmentId))}>Đóng gói</button>}
          {item.shipmentStatus === 'PACKED' && <button className="button primary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => handoverExchangeShipment(item.shipmentId))}>Bàn giao vận chuyển</button>}
          {item.shipmentStatus === 'SHIPPING' && <><button className="button primary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => recordExchangeDelivery(item.shipmentId, 'DELIVERED'))}>Giao thành công</button><button className="button secondary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => recordExchangeDelivery(item.shipmentId, 'FAILED', 'Khách chưa nhận được hàng'))}>Giao thất bại</button></>}
          {item.shipmentStatus === 'DELIVERY_FAILED' && item.redeliveryCount < 2 && <button className="button primary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => retryExchangeDelivery(item.shipmentId))}>Giao lại</button>}
          {item.shipmentStatus === 'DELIVERY_FAILED' && <button className="button secondary" disabled={busy === item.shipmentId} onClick={() => void run(item.shipmentId, () => returnExchangeStock(item.shipmentId, 'Đã nhận lại kiện giao đổi'))}>Nhận lại vào cách ly</button>}
        </div>
      </article>)}</div>}
  </section>
}
