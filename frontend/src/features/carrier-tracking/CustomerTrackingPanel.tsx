import { useState } from 'react'
import type { CustomerOrderTrackingSummary } from '../orders/orders.api'
import { getCustomerOrderTracking, safeTrackingUrl, type CarrierConsignment } from './carrier-tracking.api'

const labels: Record<string, string> = {
  DRAFT: 'Đang tạo vận đơn', LABEL_READY: 'Đã chuẩn bị nhãn', HANDED_OVER: 'Đã bàn giao hãng', PICKED_UP: 'Hãng đã lấy hàng',
  IN_TRANSIT: 'Đang trung chuyển', OUT_FOR_DELIVERY: 'Đang giao đến bạn', DELIVERY_FAILED: 'Giao chưa thành công',
  DELIVERED: 'Đã giao', RETURNING: 'Đang hoàn về cửa hàng', RETURNED: 'Đã hoàn về cửa hàng', CANCELLED: 'Đã hủy',
}
const label = (value: string) => labels[value] ?? value.replaceAll('_', ' ')
const date = (value: string) => new Date(value).toLocaleString('vi-VN')

export function CustomerTrackingPanel({ orderId, summary }: { orderId: string; summary: CustomerOrderTrackingSummary }) {
  const [detail, setDetail] = useState<CarrierConsignment | null>(null)
  const [expanded, setExpanded] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const externalUrl = safeTrackingUrl(detail?.trackingUrl ?? summary.trackingUrl)

  async function toggle() {
    if (expanded) { setExpanded(false); return }
    setExpanded(true)
    if (detail) return
    setLoading(true); setError(false)
    try { setDetail(await getCustomerOrderTracking(orderId)) }
    catch { setError(true) }
    finally { setLoading(false) }
  }

  return <section className="customer-tracking-card" aria-labelledby={`tracking-${orderId}`}>
    <div className="customer-tracking-summary">
      <span className="tracking-icon" aria-hidden="true">🚚</span>
      <div><p>Vận chuyển bởi {summary.carrierName}</p><h3 id={`tracking-${orderId}`}>{summary.trackingNumber}</h3><span>{label(summary.consignmentStatus)}</span></div>
      <div className="tracking-summary-actions">
        {externalUrl && <a href={externalUrl} target="_blank" rel="noopener noreferrer" className="text-button">Tra cứu trên website hãng <span aria-hidden="true">↗</span></a>}
        <button type="button" className="button-secondary" aria-expanded={expanded} onClick={() => void toggle()}>{expanded ? 'Thu gọn hành trình' : 'Xem hành trình'}</button>
      </div>
    </div>
    {expanded && <div className="customer-tracking-detail">
      {loading ? <p role="status">Đang tải hành trình…</p> : error ? <div role="alert" className="alert error">Chưa thể tải hành trình. <button className="text-button" type="button" onClick={() => { setDetail(null); setExpanded(false); setError(false) }}>Thử lại</button></div> : detail && (detail.events.length === 0 ? <p className="empty-hint">Hãng chưa ghi nhận mốc hành trình mới.</p> : <ol className="public-tracking-timeline">{detail.events.map((event, index) => <li key={event.id} className={index === detail.events.length - 1 ? 'latest' : ''}><span className="carrier-timeline-dot" aria-hidden="true" /><div><strong>{label(event.eventType)}</strong><time dateTime={event.occurredAt}>{date(event.occurredAt)}</time><p>{[event.location, event.description].filter(Boolean).join(' · ') || 'Đã cập nhật trạng thái vận chuyển'}</p></div></li>)}</ol>)}
    </div>}
  </section>
}
