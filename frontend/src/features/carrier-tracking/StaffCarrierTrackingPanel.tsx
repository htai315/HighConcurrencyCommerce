import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { ApiError } from '../../lib/api/client'
import {
  createStaffConsignment, getStaffConsignment, listStaffActiveCarriers, listStaffConsignments,
  recordStaffTrackingEvent, transitionStaffConsignment, uploadStaffCarrierLabel,
  type CarrierConsignment, type CarrierSummary,
} from './carrier-tracking.api'

const statusLabels: Record<string, string> = {
  DRAFT: 'Nháp', LABEL_READY: 'Đã có nhãn', HANDED_OVER: 'Đã bàn giao', IN_TRANSIT: 'Đang trung chuyển',
  OUT_FOR_DELIVERY: 'Đang giao', DELIVERY_FAILED: 'Giao thất bại', DELIVERED: 'Đã giao',
  RETURNING: 'Đang hoàn', RETURNED: 'Đã hoàn', CANCELLED: 'Đã hủy', PICKED_UP: 'Đã lấy hàng',
}
const label = (value: string) => statusLabels[value] ?? value.replaceAll('_', ' ')
const date = (value: string | null) => value ? new Date(value).toLocaleString('vi-VN') : '—'
function localDateTimeInputValue(value = new Date()): string {
  return new Date(value.getTime() - value.getTimezoneOffset() * 60_000).toISOString().slice(0, 19)
}

export function StaffCarrierTrackingPanel() {
  const [carriers, setCarriers] = useState<CarrierSummary[]>([])
  const [items, setItems] = useState<CarrierConsignment[]>([])
  const [selected, setSelected] = useState<CarrierConsignment | null>(null)
  const [filter, setFilter] = useState('')
  const [targetType, setTargetType] = useState<'SALES' | 'EXCHANGE'>('SALES')
  const [shipmentId, setShipmentId] = useState('')
  const [carrierId, setCarrierId] = useState('')
  const [trackingNumber, setTrackingNumber] = useState('')
  const [serviceCode, setServiceCode] = useState('')
  const [eventType, setEventType] = useState('IN_TRANSIT')
  const [eventLocation, setEventLocation] = useState('')
  const [eventDescription, setEventDescription] = useState('')
  const [eventOccurredAt, setEventOccurredAt] = useState(() => localDateTimeInputValue())
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [confirmAction, setConfirmAction] = useState<'handover' | 'cancel' | null>(null)
  const [notice, setNotice] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const refresh = useCallback(async (preferredId?: string) => {
    setLoading(true)
    try {
      const [activeCarriers, queue] = await Promise.all([listStaffActiveCarriers(), listStaffConsignments(filter)])
      setCarriers(activeCarriers); setItems(queue)
      setCarrierId(current => current || activeCarriers[0]?.id || '')
      const id = preferredId
      if (id) setSelected(await getStaffConsignment(id))
      else if (queue[0]) setSelected(await getStaffConsignment(queue[0].id))
      else setSelected(null)
    } catch { setNotice({ kind: 'error', text: 'Không thể tải hàng đợi vận đơn.' }) }
    finally { setLoading(false) }
  }, [filter])

  useEffect(() => {
    let ignore = false
    Promise.all([listStaffActiveCarriers(), listStaffConsignments(filter)]).then(async ([activeCarriers, queue]) => {
      const first = queue[0]
      const detail = first ? await getStaffConsignment(first.id) : null
      if (!ignore) {
        setCarriers(activeCarriers); setItems(queue); setCarrierId(current => current || activeCarriers[0]?.id || '')
        setSelected(detail); setLoading(false)
      }
    }).catch(() => { if (!ignore) { setNotice({ kind: 'error', text: 'Không thể tải hàng đợi vận đơn.' }); setLoading(false) } })
    return () => { ignore = true }
  }, [filter])

  async function select(id: string) {
    setLoading(true); setNotice(null)
    try { setSelected(await getStaffConsignment(id)) }
    catch { setNotice({ kind: 'error', text: 'Không thể tải chi tiết vận đơn.' }) }
    finally { setLoading(false) }
  }

  async function create(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setNotice(null)
    try {
      const created = await createStaffConsignment(targetType, shipmentId.trim(), { carrierId, trackingNumber: trackingNumber.trim(), serviceCode: serviceCode.trim() || null })
      setShipmentId(''); setTrackingNumber(''); setServiceCode(''); await refresh(created.id)
      setNotice({ kind: 'success', text: `Đã tạo vận đơn ${created.trackingNumber}. Hãy tải nhãn trước khi bàn giao.` })
    } catch (error) { setNotice({ kind: 'error', text: error instanceof ApiError && error.code === 'CONSIGNMENT_ALREADY_EXISTS' ? 'Shipment này đã có vận đơn.' : 'Không thể tạo vận đơn. Kiểm tra ID shipment và mã tracking.' }) }
    finally { setBusy(false) }
  }

  async function upload(file: File) {
    if (!selected) return
    if (!['application/pdf', 'image/png', 'image/jpeg'].includes(file.type) || file.size <= 0 || file.size > 5 * 1024 * 1024) {
      setNotice({ kind: 'error', text: 'Nhãn phải là PDF, PNG hoặc JPEG và không quá 5 MiB.' }); return
    }
    setBusy(true); setNotice(null)
    try { const saved = await uploadStaffCarrierLabel(selected.id, file, selected.revision); setSelected(saved); await refresh(saved.id); setNotice({ kind: 'success', text: 'Đã tải nhãn vận chuyển và kiểm tra checksum.' }) }
    catch (error) { await handleMutationError(error) } finally { setBusy(false) }
  }

  async function transition() {
    if (!selected || !confirmAction) return
    setBusy(true); setNotice(null)
    try {
      const saved = await transitionStaffConsignment(selected.id, confirmAction, selected.revision)
      setSelected(saved); await refresh(saved.id)
      if (confirmAction === 'handover') setEventOccurredAt(localDateTimeInputValue())
      setNotice({ kind: 'success', text: confirmAction === 'handover' ? 'Đã bàn giao vận đơn cho hãng vận chuyển.' : 'Đã hủy vận đơn nháp.' })
    } catch (error) { await handleMutationError(error) }
    finally { setBusy(false); setConfirmAction(null) }
  }

  async function addEvent(event: React.FormEvent) {
    event.preventDefault(); if (!selected) return
    setBusy(true); setNotice(null)
    try {
      const occurred = new Date(eventOccurredAt)
      if (Number.isNaN(occurred.getTime())) throw new Error('invalid date')
      const saved = await recordStaffTrackingEvent(selected.id, {
        eventKey: `manual-${crypto.randomUUID()}`, eventType, location: eventLocation.trim() || null,
        description: eventDescription.trim() || null, occurredAt: occurred.toISOString(), expectedRevision: selected.revision,
      })
      setSelected(saved); setEventLocation(''); setEventDescription(''); setEventOccurredAt(localDateTimeInputValue()); await refresh(saved.id)
      setNotice({ kind: 'success', text: 'Đã ghi nhận mốc tracking mới.' })
    } catch (error) { await handleMutationError(error) } finally { setBusy(false) }
  }

  async function handleMutationError(error: unknown) {
    if (selected && error instanceof ApiError && error.code === 'CONSIGNMENT_STALE') {
      setSelected(await getStaffConsignment(selected.id)); setNotice({ kind: 'error', text: 'Vận đơn vừa được cập nhật ở phiên khác. Đã tải revision mới nhất.' })
    } else setNotice({ kind: 'error', text: 'Thao tác không hợp lệ ở trạng thái hiện tại.' })
  }

  return <section className="carrier-workspace" aria-labelledby="staff-carrier-title">
    {confirmAction && selected && <ConfirmDialog title={confirmAction === 'handover' ? 'Xác nhận bàn giao?' : 'Hủy vận đơn nháp?'}
      description={confirmAction === 'handover' ? `Vận đơn ${selected.trackingNumber} sẽ chuyển sang trạng thái đã bàn giao và không thể sửa nhãn.` : `Vận đơn ${selected.trackingNumber} sẽ bị đóng.`}
      confirmLabel={confirmAction === 'handover' ? 'Xác nhận bàn giao' : 'Hủy vận đơn'} busy={busy} onCancel={() => setConfirmAction(null)} onConfirm={() => void transition()} />}
    <div className="workspace-toolbar"><div><p className="eyebrow">Carrier operations</p><h2 id="staff-carrier-title">Vận đơn & Tracking</h2><p>Tạo vận đơn từ shipment, kiểm tra nhãn và ghi nhận hành trình theo revision.</p></div><button type="button" className="button-secondary" disabled={loading} onClick={() => void refresh(selected?.id)}>{loading ? 'Đang tải…' : 'Làm mới'}</button></div>
    {notice && <div role={notice.kind === 'error' ? 'alert' : 'status'} className={`alert ${notice.kind}`}>{notice.text}</div>}
    <div className="carrier-staff-grid">
      <div className="management-card">
        <h3>Tạo vận đơn</h3>
        <form className="carrier-form" onSubmit={create}>
          <label>Loại shipment<select value={targetType} onChange={event => setTargetType(event.target.value as 'SALES' | 'EXCHANGE')}><option value="SALES">Đơn bán</option><option value="EXCHANGE">Đơn đổi hàng</option></select></label>
          <label>Shipment ID *<input required pattern="[0-9a-fA-F-]{36}" value={shipmentId} onChange={event => setShipmentId(event.target.value)} placeholder="UUID từ hàng đợi fulfillment" /></label>
          <label>Hãng vận chuyển *<select required value={carrierId} onChange={event => setCarrierId(event.target.value)}><option value="">Chọn hãng</option>{carriers.map(carrier => <option key={carrier.id} value={carrier.id}>{carrier.name} ({carrier.code})</option>)}</select></label>
          <label>Mã tracking *<input required minLength={3} maxLength={100} value={trackingNumber} onChange={event => setTrackingNumber(event.target.value)} /></label>
          <label>Mã dịch vụ<input maxLength={64} value={serviceCode} onChange={event => setServiceCode(event.target.value)} placeholder="EXPRESS, STANDARD…" /></label>
          <button type="submit" className="button-primary" disabled={busy || carriers.length === 0}>{busy ? 'Đang tạo…' : 'Tạo vận đơn'}</button>
        </form>
      </div>
      <div className="management-card carrier-queue-panel">
        <div className="carrier-section-heading"><h3>Hàng đợi vận đơn</h3><label>Lọc trạng thái<select value={filter} onChange={event => setFilter(event.target.value)}><option value="">Tất cả</option>{['DRAFT', 'LABEL_READY', 'HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY', 'DELIVERY_FAILED', 'RETURNING'].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label></div>
        <div className="carrier-list">{items.length === 0 ? <p className="empty-hint">Không có vận đơn trong hàng đợi.</p> : items.map(item => <button type="button" key={item.id} className={selected?.id === item.id ? 'carrier-list-item active' : 'carrier-list-item'} onClick={() => void select(item.id)}><span><strong>{item.trackingNumber}</strong><small>{item.carrierName} · {item.targetType === 'SALES' ? 'Đơn bán' : 'Đơn đổi'}</small></span><span className="badge badge-draft">{label(item.consignmentStatus)}</span></button>)}</div>
      </div>
    </div>

    {selected && <div className="management-card carrier-consignment-detail">
      <div className="carrier-detail-heading"><div><p className="eyebrow">{selected.carrierCode} · Revision {selected.revision}</p><h3>{selected.trackingNumber}</h3><p>{selected.shippingMethodName ?? 'Chưa có tên phương thức'} · {selected.serviceCode ?? 'Không mã dịch vụ'}</p></div><span className="badge badge-published">{label(selected.consignmentStatus)}</span></div>
      <dl className="carrier-facts"><div><dt>Người nhận</dt><dd>{selected.recipientName} · {selected.recipientPhone}</dd></div><div><dt>Địa chỉ</dt><dd>{selected.destination}</dd></div><div><dt>Bàn giao</dt><dd>{date(selected.handedOverAt)}</dd></div><div><dt>Nhãn</dt><dd>{selected.label ? `${selected.label.contentType} · ${Math.ceil(selected.label.sizeBytes / 1024)} KiB` : 'Chưa tải nhãn'}</dd></div></dl>
      <div className="carrier-actions">
        {(selected.consignmentStatus === 'DRAFT' || selected.consignmentStatus === 'LABEL_READY') && <label className="button-secondary carrier-file-button">{selected.label ? 'Thay nhãn' : 'Tải nhãn'}<input type="file" accept="application/pdf,image/png,image/jpeg" disabled={busy} onChange={event => { const file = event.target.files?.[0]; if (file) void upload(file); event.currentTarget.value = '' }} /></label>}
        {selected.label && <a className="button-secondary" href={`/api/v1/staff/carrier-consignments/${encodeURIComponent(selected.id)}/label`}>Tải xuống nhãn</a>}
        {selected.consignmentStatus === 'LABEL_READY' && <button className="button-primary" type="button" onClick={() => setConfirmAction('handover')}>Bàn giao hãng</button>}
        {(selected.consignmentStatus === 'DRAFT' || selected.consignmentStatus === 'LABEL_READY') && <button className="button-danger" type="button" onClick={() => setConfirmAction('cancel')}>Hủy vận đơn</button>}
      </div>
      {['HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY', 'DELIVERY_FAILED'].includes(selected.consignmentStatus) && <form className="carrier-event-form" onSubmit={addEvent}>
        <h4>Ghi nhận mốc thủ công</h4>
        <label>Sự kiện<select value={eventType} onChange={event => setEventType(event.target.value)}>{['PICKED_UP', 'IN_TRANSIT', 'OUT_FOR_DELIVERY', 'DELIVERY_FAILED', 'RETURNING'].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
        <label>Thời điểm<input required type="datetime-local" step="1" value={eventOccurredAt} onChange={event => setEventOccurredAt(event.target.value)} /></label>
        <label>Vị trí<input maxLength={200} value={eventLocation} onChange={event => setEventLocation(event.target.value)} /></label>
        <label>Mô tả<input maxLength={500} value={eventDescription} onChange={event => setEventDescription(event.target.value)} /></label>
        <button type="submit" className="button-primary" disabled={busy}>Ghi nhận sự kiện</button>
      </form>}
      <div className="carrier-timeline"><h4>Lịch sử tracking</h4>{selected.events.length === 0 ? <p className="empty-hint">Chưa có mốc hành trình.</p> : <ol>{selected.events.map(item => <li key={item.id}><span className="carrier-timeline-dot" aria-hidden="true" /><div><strong>{label(item.eventType)}</strong><time dateTime={item.occurredAt}>{date(item.occurredAt)}</time><p>{[item.location, item.description].filter(Boolean).join(' · ') || 'Không có ghi chú'}</p><small>{item.sourceType === 'SYSTEM' ? 'Hệ thống' : 'Nhân viên'}</small></div></li>)}</ol>}</div>
    </div>}
  </section>
}
