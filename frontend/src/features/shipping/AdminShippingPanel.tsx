import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { ApiError } from '../../lib/api/client'
import { formatPrice } from '../catalog/catalog.api'
import {
  createAdminShippingMethod,
  getAdminShippingMethod,
  listAdminShippingMethods,
  removeAdminShippingProvinceRate,
  transitionAdminShippingMethod,
  updateAdminShippingMethod,
  upsertAdminShippingProvinceRate,
  type AdminShippingMethodDetail,
  type AdminShippingMethodSummary,
  type ShippingMethodPayload,
} from './admin-shipping.api'

type Notice = { type: 'success' | 'error'; text: string }
type FormValues = { code: string; name: string; description: string; baseFee: string; threshold: string; minDays: string; maxDays: string; displayOrder: string }

function initialValues(method?: AdminShippingMethodDetail): FormValues {
  return {
    code: method?.code ?? '', name: method?.name ?? '', description: method?.description ?? '',
    baseFee: String(method?.baseFee ?? 30000), threshold: method?.freeShippingThreshold == null ? '' : String(method.freeShippingThreshold),
    minDays: String(method?.estimatedMinDays ?? 2), maxDays: String(method?.estimatedMaxDays ?? 4), displayOrder: String(method?.displayOrder ?? 10),
  }
}

function ShippingMethodForm({ method, busy, onCancel, onSubmit }: {
  method?: AdminShippingMethodDetail; busy: boolean; onCancel: () => void; onSubmit: (payload: ShippingMethodPayload) => Promise<void>
}) {
  const [values, setValues] = useState(() => initialValues(method))
  const [error, setError] = useState<string | null>(null)
  function set(key: keyof FormValues, value: string) { setValues(current => ({ ...current, [key]: value })) }
  async function submit(event: React.FormEvent) {
    event.preventDefault(); setError(null)
    const baseFee = Number(values.baseFee); const threshold = values.threshold.trim() ? Number(values.threshold) : null
    const min = Number(values.minDays); const max = Number(values.maxDays); const order = Number(values.displayOrder)
    if (!values.name.trim() || (!method && !/^[A-Z0-9_-]{3,32}$/.test(values.code.trim().toUpperCase()))) return setError('Mã cần 3–32 ký tự A–Z/0–9/_/- và tên không được trống.')
    if (![baseFee, min, max, order].every(Number.isFinite) || baseFee < 0 || min < 0 || max < min || order < 0) return setError('Phí, ETA hoặc thứ tự hiển thị không hợp lệ.')
    if (threshold !== null && (!Number.isFinite(threshold) || threshold <= 0)) return setError('Ngưỡng miễn phí phải lớn hơn 0 hoặc để trống.')
    await onSubmit({
      ...(!method ? { code: values.code.trim().toUpperCase() } : {}), name: values.name.trim(), description: values.description.trim() || null,
      baseFee, freeShippingThreshold: threshold, estimatedMinDays: min, estimatedMaxDays: max, displayOrder: order,
      ...(method ? { expectedRevision: method.revision } : {}),
    })
  }
  return <form className="shipping-admin-form" onSubmit={event => void submit(event)}>
    <h3>{method ? `Chỉnh sửa ${method.code}` : 'Tạo phương thức mới'}</h3>
    <div className="shipping-form-grid">
      <label>Mã phương thức<input value={values.code} disabled={Boolean(method) || busy} maxLength={32} onChange={event => set('code', event.target.value.toUpperCase())} placeholder="STANDARD" required /></label>
      <label>Tên hiển thị<input value={values.name} disabled={busy} maxLength={120} onChange={event => set('name', event.target.value)} required /></label>
      <label className="wide">Mô tả<textarea value={values.description} disabled={busy} maxLength={500} rows={2} onChange={event => set('description', event.target.value)} /></label>
      <label>Phí cơ bản (VNĐ)<input type="number" min="0" step="1" value={values.baseFee} disabled={busy} onChange={event => set('baseFee', event.target.value)} required /></label>
      <label>Miễn phí từ (VNĐ)<input type="number" min="1" step="1" value={values.threshold} disabled={busy} onChange={event => set('threshold', event.target.value)} placeholder="Không áp dụng" /></label>
      <label>ETA tối thiểu (ngày)<input type="number" min="0" max="365" value={values.minDays} disabled={busy} onChange={event => set('minDays', event.target.value)} required /></label>
      <label>ETA tối đa (ngày)<input type="number" min="0" max="365" value={values.maxDays} disabled={busy} onChange={event => set('maxDays', event.target.value)} required /></label>
      <label>Thứ tự hiển thị<input type="number" min="0" value={values.displayOrder} disabled={busy} onChange={event => set('displayOrder', event.target.value)} required /></label>
    </div>
    {error && <p className="alert error" role="alert">{error}</p>}
    <div className="shipping-form-actions"><button type="button" className="button-secondary" disabled={busy} onClick={onCancel}>Hủy</button><button type="submit" className="button-primary" disabled={busy}>{busy ? 'Đang lưu…' : method ? 'Lưu cấu hình' : 'Tạo phương thức'}</button></div>
  </form>
}

export function AdminShippingPanel() {
  const [methods, setMethods] = useState<AdminShippingMethodSummary[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<AdminShippingMethodDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<Notice | null>(null)
  const [showCreate, setShowCreate] = useState(false)
  const [editing, setEditing] = useState(false)
  const [province, setProvince] = useState('')
  const [surcharge, setSurcharge] = useState('0')
  const [rateActive, setRateActive] = useState(true)
  const [confirm, setConfirm] = useState<{ title: string; description: string; run: () => Promise<unknown> } | null>(null)

  const loadMethods = useCallback(async (preferredId?: string) => {
    const items = await listAdminShippingMethods()
    setMethods(items)
    setSelectedId(current => preferredId ?? (current && items.some(item => item.id === current) ? current : items[0]?.id ?? null))
  }, [])

  useEffect(() => {
    let ignore = false
    listAdminShippingMethods().then(items => {
      if (ignore) return
      setMethods(items); setSelectedId(items[0]?.id ?? null)
    }).catch(error => { if (!ignore) setNotice({ type: 'error', text: error instanceof Error ? error.message : 'Không tải được phương thức vận chuyển.' }) })
      .finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [])

  useEffect(() => {
    if (!selectedId) return
    let ignore = false
    getAdminShippingMethod(selectedId).then(value => { if (!ignore) { setDetail(value); setEditing(false) } })
      .catch(error => { if (!ignore) setNotice({ type: 'error', text: error instanceof Error ? error.message : 'Không tải được chi tiết phương thức.' }) })
    return () => { ignore = true }
  }, [selectedId])

  async function runMutation(action: () => Promise<AdminShippingMethodDetail>, success: string) {
    setBusy(true); setNotice(null)
    try {
      const updated = await action()
      setDetail(updated); setEditing(false); setNotice({ type: 'success', text: success })
      await loadMethods(updated.id)
      return true
    } catch (error: unknown) {
      if (error instanceof ApiError && error.code === 'SHIPPING_METHOD_STALE') {
        if (selectedId) setDetail(await getAdminShippingMethod(selectedId))
        await loadMethods(selectedId ?? undefined)
        setEditing(false)
        setNotice({ type: 'error', text: 'Cấu hình đã được Admin khác cập nhật. Dữ liệu mới nhất đã được tải lại; thay đổi của bạn chưa được ghi đè.' })
      } else setNotice({ type: 'error', text: error instanceof Error ? error.message : 'Không thể cập nhật phương thức vận chuyển.' })
      return false
    } finally { setBusy(false) }
  }

  function requestTransition(action: 'activate' | 'deactivate' | 'make-default') {
    if (!detail) return
    const labels = { activate: 'Kích hoạt', deactivate: 'Ngừng hoạt động', 'make-default': 'Đặt làm mặc định' }
    setConfirm({
      title: `${labels[action]} ${detail.name}?`,
      description: action === 'deactivate' ? 'Phương thức sẽ không còn xuất hiện trong báo giá mới. Snapshot đơn cũ không thay đổi.' : 'Thay đổi có hiệu lực cho các báo giá và checkout mới.',
      run: () => runMutation(() => transitionAdminShippingMethod(detail.id, action, detail.revision), `Đã ${labels[action].toLocaleLowerCase('vi')} “${detail.name}”.`),
    })
  }

  async function saveRate(event: React.FormEvent) {
    event.preventDefault()
    if (!detail || !province.trim() || !Number.isFinite(Number(surcharge)) || Number(surcharge) < 0) return setNotice({ type: 'error', text: 'Tỉnh/thành và phụ phí phải hợp lệ.' })
    const saved = await runMutation(() => upsertAdminShippingProvinceRate(detail.id, province.trim(), Number(surcharge), rateActive, detail.revision), `Đã lưu phụ phí cho ${province.trim()}.`)
    if (saved) { setProvince(''); setSurcharge('0'); setRateActive(true) }
  }

  return <section className="admin-shipping-panel">
    {confirm && <ConfirmDialog title={confirm.title} description={confirm.description} confirmLabel="Xác nhận" busy={busy} onCancel={() => setConfirm(null)} onConfirm={() => void confirm.run().finally(() => setConfirm(null))} />}
    <header className="shipping-admin-header"><div><h2>Vận chuyển &amp; Phí</h2><p>Quản lý phương thức, ETA, ngưỡng miễn phí và phụ phí theo tỉnh.</p></div><button type="button" className="button-primary" onClick={() => { setShowCreate(true); setEditing(false) }}>Tạo phương thức</button></header>
    {notice && <p role={notice.type === 'error' ? 'alert' : 'status'} className={`alert ${notice.type}`}>{notice.text}</p>}
    {showCreate && <ShippingMethodForm busy={busy} onCancel={() => setShowCreate(false)} onSubmit={async payload => { if (await runMutation(() => createAdminShippingMethod(payload), 'Đã tạo phương thức ở trạng thái chưa kích hoạt.')) setShowCreate(false) }} />}
    <div className="shipping-admin-layout">
      <aside className="shipping-method-admin-list" aria-label="Danh sách phương thức vận chuyển">
        {loading ? <p role="status">Đang tải phương thức…</p> : methods.length === 0 ? <p>Chưa có phương thức vận chuyển.</p> : methods.map(method => <button key={method.id} type="button" className={method.id === selectedId ? 'is-selected' : ''} onClick={() => setSelectedId(method.id)}>
          <span><strong>{method.name}</strong><small>{method.code} · revision {method.revision}</small></span><span><small className={`badge ${method.methodStatus === 'ACTIVE' ? 'badge-success' : 'badge-muted'}`}>{method.methodStatus === 'ACTIVE' ? 'Đang dùng' : 'Chưa dùng'}</small>{method.isDefault && <small className="shipping-default-mark">Mặc định</small>}</span>
          <span className="shipping-list-price">{formatPrice(method.baseFee)} · {method.estimatedMinDays}–{method.estimatedMaxDays} ngày</span>
        </button>)}
      </aside>
      <div className="shipping-admin-detail">
        {!detail ? <div className="empty-state"><h3>Chọn một phương thức</h3><p>Xem và cập nhật cấu hình vận chuyển.</p></div> : editing ? <ShippingMethodForm key={`${detail.id}:${detail.revision}`} method={detail} busy={busy} onCancel={() => setEditing(false)} onSubmit={async payload => { await runMutation(() => updateAdminShippingMethod(detail.id, payload), `Đã cập nhật “${detail.name}”.`) }} /> : <>
          <article className="management-card shipping-method-overview"><header><div><p className="eyebrow">{detail.code}</p><h3>{detail.name}</h3></div><span className={`badge ${detail.methodStatus === 'ACTIVE' ? 'badge-success' : 'badge-muted'}`}>{detail.methodStatus}</span></header><p>{detail.description || 'Chưa có mô tả.'}</p><dl><div><dt>Phí cơ bản</dt><dd>{formatPrice(detail.baseFee)}</dd></div><div><dt>Miễn phí từ</dt><dd>{detail.freeShippingThreshold ? formatPrice(detail.freeShippingThreshold) : 'Không áp dụng'}</dd></div><div><dt>ETA</dt><dd>{detail.estimatedMinDays}–{detail.estimatedMaxDays} ngày</dd></div><div><dt>Revision</dt><dd>{detail.revision}</dd></div></dl>
            <div className="shipping-method-actions"><button type="button" className="button-secondary" onClick={() => setEditing(true)}>Chỉnh sửa</button>{detail.methodStatus === 'INACTIVE' ? <button type="button" className="button-secondary" onClick={() => requestTransition('activate')}>Kích hoạt</button> : <button type="button" className="button-secondary" disabled={detail.isDefault} onClick={() => requestTransition('deactivate')}>Ngừng hoạt động</button>}{detail.methodStatus === 'ACTIVE' && !detail.isDefault && <button type="button" className="button-primary" onClick={() => requestTransition('make-default')}>Đặt mặc định</button>}</div>
          </article>
          <article className="management-card shipping-rate-workspace"><header><div><h3>Phụ phí theo tỉnh/thành</h3><p>Province key sẽ được server chuẩn hóa khi lưu.</p></div><span>{detail.provinceRates.length} cấu hình</span></header>
            <form onSubmit={event => void saveRate(event)} className="shipping-rate-form"><label>Tỉnh/thành<input value={province} maxLength={160} disabled={busy} onChange={event => setProvince(event.target.value)} placeholder="Ví dụ: Hà Nội" required /></label><label>Phụ phí (VNĐ)<input type="number" min="0" step="1" value={surcharge} disabled={busy} onChange={event => setSurcharge(event.target.value)} required /></label><label className="shipping-rate-check"><input type="checkbox" checked={rateActive} disabled={busy} onChange={event => setRateActive(event.target.checked)} /> Áp dụng</label><button type="submit" className="button-primary" disabled={busy}>Lưu phụ phí</button></form>
            <div className="table-responsive"><table className="admin-table"><thead><tr><th>Tỉnh/thành</th><th>Phụ phí</th><th>Trạng thái</th><th /></tr></thead><tbody>{detail.provinceRates.length === 0 ? <tr><td colSpan={4}>Chưa có phụ phí riêng; hệ thống dùng phí cơ bản.</td></tr> : detail.provinceRates.map(rate => <tr key={rate.id}><td>{rate.provinceKey}</td><td>{formatPrice(rate.surchargeFee)}</td><td>{rate.isActive ? 'Áp dụng' : 'Tạm tắt'}</td><td><button type="button" className="text-button" disabled={busy} onClick={() => setConfirm({ title: `Xóa phụ phí ${rate.provinceKey}?`, description: 'Báo giá mới sẽ quay về phí cơ bản cho tỉnh này.', run: () => runMutation(() => removeAdminShippingProvinceRate(detail.id, rate.provinceKey, detail.revision), `Đã xóa phụ phí ${rate.provinceKey}.`) })}>Xóa</button></td></tr>)}</tbody></table></div>
          </article>
        </>}
      </div>
    </div>
  </section>
}
