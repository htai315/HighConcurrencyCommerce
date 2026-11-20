import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { ApiError } from '../../lib/api/client'
import {
  createAdminCarrier, getAdminCarrier, listAdminCarriers, transitionAdminCarrier, updateAdminCarrier,
  type CarrierDetail, type CarrierPayload, type CarrierSummary,
} from './carrier-tracking.api'

const emptyForm: CarrierPayload = { code: '', name: '', hotline: null, trackingUrlTemplate: null }
const date = (value: string) => new Date(value).toLocaleString('vi-VN')

export function AdminCarriersPanel() {
  const [items, setItems] = useState<CarrierSummary[]>([])
  const [selected, setSelected] = useState<CarrierDetail | null>(null)
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('')
  const [form, setForm] = useState<CarrierPayload>(emptyForm)
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [confirmAction, setConfirmAction] = useState<'activate' | 'deactivate' | null>(null)
  const [notice, setNotice] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const load = useCallback(async (preferredId?: string) => {
    setLoading(true)
    try {
      const next = await listAdminCarriers(query, status); setItems(next)
      const id = preferredId ?? next[0]?.id
      setSelected(id ? await getAdminCarrier(id) : null)
      setNotice(null)
    } catch { setNotice({ kind: 'error', text: 'Không thể tải danh sách hãng vận chuyển.' }) }
    finally { setLoading(false) }
  }, [query, status])

  useEffect(() => {
    let ignore = false
    listAdminCarriers(query, status).then(async next => {
      const first = next[0]
      const detail = first ? await getAdminCarrier(first.id) : null
      if (!ignore) { setItems(next); setSelected(detail); setNotice(null); setLoading(false) }
    }).catch(() => { if (!ignore) { setNotice({ kind: 'error', text: 'Không thể tải danh sách hãng vận chuyển.' }); setLoading(false) } })
    return () => { ignore = true }
  }, [query, status])

  async function choose(id: string) {
    setLoading(true); setNotice(null)
    try { setSelected(await getAdminCarrier(id)); setCreating(false); setEditing(false) }
    catch { setNotice({ kind: 'error', text: 'Không thể tải cấu hình hãng.' }) }
    finally { setLoading(false) }
  }

  function edit() {
    if (!selected) return
    setForm({ name: selected.name, hotline: selected.hotline, trackingUrlTemplate: selected.trackingUrlTemplate, expectedRevision: selected.revision })
    setEditing(true); setCreating(false)
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setNotice(null)
    try {
      const payload = { ...form, hotline: form.hotline?.trim() || null, trackingUrlTemplate: form.trackingUrlTemplate?.trim() || null }
      const saved = creating ? await createAdminCarrier(payload) : await updateAdminCarrier(selected!.id, { ...payload, expectedRevision: selected!.revision })
      setSelected(saved); setCreating(false); setEditing(false); setForm(emptyForm)
      await load(saved.id); setNotice({ kind: 'success', text: creating ? 'Đã tạo hãng vận chuyển.' : 'Đã cập nhật cấu hình hãng.' })
    } catch (error) {
      if (error instanceof ApiError && error.code === 'CARRIER_STALE' && selected) {
        setSelected(await getAdminCarrier(selected.id)); setEditing(false)
        setNotice({ kind: 'error', text: 'Cấu hình đã thay đổi ở phiên khác. Dữ liệu mới nhất đã được tải lại.' })
      } else setNotice({ kind: 'error', text: error instanceof ApiError && error.code === 'CARRIER_CODE_CONFLICT' ? 'Mã hãng đã tồn tại.' : 'Không thể lưu hãng vận chuyển.' })
    } finally { setBusy(false) }
  }

  async function transition() {
    if (!selected || !confirmAction) return
    setBusy(true)
    try {
      const saved = await transitionAdminCarrier(selected.id, confirmAction, selected.revision)
      setSelected(saved); await load(saved.id)
      setNotice({ kind: 'success', text: confirmAction === 'activate' ? 'Đã kích hoạt hãng vận chuyển.' : 'Đã ngừng nhận vận đơn mới cho hãng.' })
    } catch (error) {
      if (error instanceof ApiError && error.code === 'CARRIER_STALE') {
        setSelected(await getAdminCarrier(selected.id)); setNotice({ kind: 'error', text: 'Revision đã cũ; dữ liệu mới nhất đã được tải lại.' })
      } else setNotice({ kind: 'error', text: 'Không thể đổi trạng thái hãng.' })
    } finally { setBusy(false); setConfirmAction(null) }
  }

  return <section className="carrier-workspace" aria-labelledby="carrier-admin-title">
    {confirmAction && selected && <ConfirmDialog
      title={confirmAction === 'activate' ? 'Kích hoạt hãng vận chuyển?' : 'Ngừng hãng vận chuyển?'}
      description={confirmAction === 'activate' ? `${selected.name} sẽ có thể nhận vận đơn mới.` : `Vận đơn đang chạy vẫn được theo dõi; Staff không thể tạo vận đơn mới với ${selected.name}.`}
      confirmLabel={confirmAction === 'activate' ? 'Kích hoạt' : 'Ngừng hãng'} busy={busy}
      onCancel={() => setConfirmAction(null)} onConfirm={() => void transition()} />}
    <div className="workspace-toolbar">
      <div><p className="eyebrow">Carrier registry</p><h2 id="carrier-admin-title">Hãng vận chuyển</h2><p>Quản lý kết nối tracking và vòng đời hãng mà không ảnh hưởng vận đơn lịch sử.</p></div>
      <button className="button-primary" type="button" onClick={() => { setCreating(true); setEditing(false); setForm(emptyForm); setSelected(null) }}>+ Thêm hãng</button>
    </div>
    {notice && <div role={notice.kind === 'error' ? 'alert' : 'status'} className={`alert ${notice.kind}`}>{notice.text}</div>}
    <div className="carrier-layout">
      <aside className="management-card carrier-list-panel">
        <div className="carrier-filters">
          <label>Tìm hãng<input value={query} onChange={event => setQuery(event.target.value)} placeholder="Mã hoặc tên hãng" /></label>
          <label>Trạng thái<select value={status} onChange={event => setStatus(event.target.value)}><option value="">Tất cả</option><option value="ACTIVE">Đang hoạt động</option><option value="INACTIVE">Ngừng hoạt động</option></select></label>
        </div>
        <button type="button" className="button-secondary" onClick={() => void load(selected?.id)} disabled={loading}>Lọc danh sách</button>
        <div className="carrier-list" aria-label="Danh sách hãng vận chuyển">
          {loading && items.length === 0 ? <p>Đang tải…</p> : items.length === 0 ? <p className="empty-hint">Chưa có hãng phù hợp.</p> : items.map(item =>
            <button key={item.id} type="button" className={selected?.id === item.id ? 'carrier-list-item active' : 'carrier-list-item'} onClick={() => void choose(item.id)}>
              <span><strong>{item.name}</strong><small>{item.code} · {item.consignmentCount} vận đơn</small></span>
              <span className={`badge ${item.carrierStatus === 'ACTIVE' ? 'badge-published' : 'badge-draft'}`}>{item.carrierStatus === 'ACTIVE' ? 'Hoạt động' : 'Đã ngừng'}</span>
            </button>)}
        </div>
      </aside>

      <div className="management-card carrier-detail-panel">
        {(creating || editing) ? <form className="carrier-form" onSubmit={submit}>
          <div><p className="eyebrow">{creating ? 'New carrier' : `Revision ${selected?.revision}`}</p><h3>{creating ? 'Thêm hãng vận chuyển' : `Chỉnh sửa ${selected?.name}`}</h3></div>
          {creating && <label>Mã hãng *<input required minLength={2} maxLength={32} pattern="[A-Za-z0-9_-]{2,32}" value={form.code ?? ''} onChange={event => setForm(current => ({ ...current, code: event.target.value.toUpperCase() }))} /></label>}
          <label>Tên hãng *<input required minLength={2} maxLength={120} value={form.name} onChange={event => setForm(current => ({ ...current, name: event.target.value }))} /></label>
          <label>Hotline<input maxLength={32} value={form.hotline ?? ''} onChange={event => setForm(current => ({ ...current, hotline: event.target.value }))} /></label>
          <label>Mẫu URL tracking<input type="url" placeholder="https://carrier.vn/track/{trackingNumber}" maxLength={500} value={form.trackingUrlTemplate ?? ''} onChange={event => setForm(current => ({ ...current, trackingUrlTemplate: event.target.value }))} /><small>Dùng placeholder {'{trackingNumber}'}. Chỉ URL HTTPS mới được hiển thị cho khách.</small></label>
          <div className="form-actions"><button className="button-secondary" type="button" disabled={busy} onClick={() => { setCreating(false); setEditing(false); setForm(emptyForm); if (!selected && items[0]) void choose(items[0].id) }}>Hủy</button><button type="submit" className="button-primary" disabled={busy}>{busy ? 'Đang lưu…' : creating ? 'Tạo hãng' : 'Lưu thay đổi'}</button></div>
        </form> : selected ? <>
          <div className="carrier-detail-heading"><div><p className="eyebrow">{selected.code}</p><h3>{selected.name}</h3></div><span className={`badge ${selected.carrierStatus === 'ACTIVE' ? 'badge-published' : 'badge-draft'}`}>{selected.carrierStatus === 'ACTIVE' ? 'Đang hoạt động' : 'Ngừng hoạt động'}</span></div>
          <dl className="carrier-facts"><div><dt>Hotline</dt><dd>{selected.hotline ?? 'Chưa cấu hình'}</dd></div><div><dt>Revision</dt><dd>{selected.revision}</dd></div><div><dt>Cập nhật</dt><dd>{date(selected.updatedAt)}</dd></div><div><dt>Tracking URL</dt><dd>{selected.trackingUrlTemplate ?? 'Chưa cấu hình'}</dd></div></dl>
          <div className="form-actions"><button className="button-secondary" type="button" onClick={edit}>Chỉnh sửa</button><button className={selected.carrierStatus === 'ACTIVE' ? 'button-danger' : 'button-primary'} type="button" onClick={() => setConfirmAction(selected.carrierStatus === 'ACTIVE' ? 'deactivate' : 'activate')}>{selected.carrierStatus === 'ACTIVE' ? 'Ngừng hãng' : 'Kích hoạt'}</button></div>
        </> : <div className="carrier-empty"><span aria-hidden="true">🚚</span><h3>Chọn một hãng để xem cấu hình</h3><p>Thông tin revision giúp ngăn ghi đè khi nhiều Admin cùng chỉnh sửa.</p></div>}
      </div>
    </div>
  </section>
}
