import { useCallback, useEffect, useMemo, useState } from 'react'
import { approveRevision, getAdminRevisionReview, getRevisionQueue, rejectRevision, type ProductRevisionReview, type ProductRevisionSummary } from './product-revisions.api'

type ReviewTab = 'content' | 'images' | 'specifications'
const changeLabel: Record<string, string> = { ADDED: 'Thêm mới', MODIFIED: 'Thay đổi', REORDERED: 'Đổi thứ tự', REMOVED: 'Gỡ bỏ', UNCHANGED: 'Không đổi' }

function Value({ value }: { value: string | null }) {
  return <span className={value ? undefined : 'revision-empty-value'}>{value || 'Chưa thiết lập'}</span>
}

export function AdminProductRevisionsPanel() {
  const [items, setItems] = useState<ProductRevisionSummary[]>([])
  const [selected, setSelected] = useState<ProductRevisionReview | null>(null)
  const [tab, setTab] = useState<ReviewTab>('content')
  const [changesOnly, setChangesOnly] = useState(true)
  const [rejectReason, setRejectReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  const refresh = useCallback(async () => { setItems(await getRevisionQueue()); setSelected(null) }, [])
  useEffect(() => { void Promise.resolve().then(refresh).catch(error => setMessage(error instanceof Error ? error.message : 'Không tải được queue.')) }, [refresh])

  async function openReview(id: string) {
    setBusy(true); setMessage(null)
    try { setSelected(await getAdminRevisionReview(id)); setRejectReason(''); setTab('content') }
    catch (error) { setMessage(error instanceof Error ? error.message : 'Không tải được bản so sánh.') }
    finally { setBusy(false) }
  }

  async function decide(kind: 'approve' | 'reject') {
    if (!selected) return
    if (kind === 'reject' && rejectReason.trim().length < 3) { setMessage('Lý do từ chối phải có ít nhất 3 ký tự.'); return }
    setBusy(true)
    try {
      if (kind === 'approve') await approveRevision(selected.revisionId, 'Đã kiểm tra nội dung')
      else await rejectRevision(selected.revisionId, rejectReason.trim())
      setMessage(kind === 'approve' ? 'Đã duyệt và cập nhật Storefront.' : 'Đã từ chối revision.')
      setRejectReason(''); await refresh()
    } catch (error) { setMessage(error instanceof Error ? error.message : 'Không thể xử lý revision.') }
    finally { setBusy(false) }
  }

  const content = useMemo(() => selected?.content.filter(item => !changesOnly || item.changed) ?? [], [changesOnly, selected])
  const images = useMemo(() => selected?.images.filter(item => !changesOnly || item.changeType !== 'UNCHANGED') ?? [], [changesOnly, selected])
  const specifications = useMemo(() => selected?.specifications.filter(item => !changesOnly || item.changeType !== 'UNCHANGED') ?? [], [changesOnly, selected])

  return <div className="revision-admin-grid">
    <section className="management-card revision-queue" aria-label="Revision chờ duyệt">
      <div className="revision-panel-title"><div><p className="eyebrow">Kiểm duyệt nội dung</p><h2>Revision chờ duyệt</h2></div><span className="revision-count">{items.length}</span></div>
      {items.length === 0 ? <p className="revision-empty">Không có bản chỉnh sửa đang chờ.</p> : items.map(item => <button className={`revision-queue-item ${selected?.revisionId === item.id ? 'is-active' : ''}`} disabled={busy} key={item.id} onClick={() => void openReview(item.id)}><strong>{item.productTitle}</strong><span>Revision #{item.revisionNo} · nền v{item.baseContentVersion}</span><small>{item.submittedAt ? new Date(item.submittedAt).toLocaleString('vi-VN') : 'Chưa gửi duyệt'}</small></button>)}
    </section>
    <section className="management-card revision-review-workspace">
      {message && <p className="form-alert" role="alert">{message}</p>}
      {!selected ? <div className="revision-empty"><h2>Chọn một revision</h2><p>Bản so sánh live và đề xuất sẽ xuất hiện tại đây.</p></div> : <>
        <div className="revision-heading"><div><p className="eyebrow">Revision #{selected.revisionNo}</p><h2>So sánh trước khi duyệt</h2></div><span className="badge badge-pending_review">{selected.revisionStatus}</span></div>
        {selected.isStale && <div className="revision-warning" role="alert"><strong>Revision đã lỗi thời.</strong> Nội dung live đang ở v{selected.currentContentVersion}, khác nền v{selected.baseContentVersion}. Hãy yêu cầu Staff tạo lại revision.</div>}
        {selected.productStatus === 'SUSPENDED' && <div className="revision-info">Sản phẩm đang tạm ngừng bán. Duyệt chỉ cập nhật nội dung, không tự mở bán lại.</div>}
        <div className="revision-toolbar"><div className="revision-tabs" role="tablist" aria-label="Nhóm thay đổi"><button className={tab === 'content' ? 'is-active' : ''} role="tab" aria-selected={tab === 'content'} onClick={() => setTab('content')}>Nội dung</button><button className={tab === 'images' ? 'is-active' : ''} role="tab" aria-selected={tab === 'images'} onClick={() => setTab('images')}>Hình ảnh</button><button className={tab === 'specifications' ? 'is-active' : ''} role="tab" aria-selected={tab === 'specifications'} onClick={() => setTab('specifications')}>Thông số</button></div><label className="revision-filter"><input type="checkbox" checked={changesOnly} onChange={event => setChangesOnly(event.target.checked)} /> Chỉ hiện thay đổi</label></div>
        <div className="revision-diff-list" role="tabpanel">
          {tab === 'content' && (content.length === 0 ? <p>Không có thay đổi nội dung.</p> : content.map(item => <article className={`revision-diff-row ${item.changed ? 'is-changed' : ''}`} key={item.field}><header><strong>{item.label}</strong><span>{item.changed ? 'Thay đổi' : 'Không đổi'}</span></header><div className="revision-before-after"><div><small>Đang công khai</small><Value value={item.currentValue} /></div><div><small>Đề xuất</small><Value value={item.proposedValue} /></div></div></article>))}
          {tab === 'images' && (images.length === 0 ? <p>Không có thay đổi hình ảnh.</p> : images.map((item, index) => <article className="revision-diff-row" key={item.revisionImageId ?? item.liveImageId ?? index}><header><strong>Ảnh #{index + 1}</strong><span>{changeLabel[item.changeType] ?? item.changeType}</span></header><div className="revision-image-compare"><figure>{item.currentFileKey ? <img src={item.currentFileKey} alt={item.currentAltText ?? 'Ảnh hiện tại'} /> : <div className="revision-image-placeholder">Không có ảnh live</div>}<figcaption>Live · vị trí {item.currentDisplayOrder ?? '—'}</figcaption></figure><figure>{item.previewAvailable && item.proposedFileKey ? <img src={item.proposedFileKey} alt={item.proposedAltText ?? 'Ảnh đề xuất'} /> : <div className="revision-image-placeholder">{item.cleanupStatus === 'COMPLETED' ? 'Ảnh đã được dọn khỏi storage' : 'Không có ảnh đề xuất'}</div>}<figcaption>Đề xuất · vị trí {item.proposedDisplayOrder ?? '—'}</figcaption></figure></div></article>))}
          {tab === 'specifications' && (specifications.length === 0 ? <p>Không có thay đổi thông số.</p> : specifications.map(item => <article className="revision-diff-row" key={item.key}><header><strong>{item.proposedDisplayName ?? item.currentDisplayName ?? item.key}</strong><span>{changeLabel[item.changeType] ?? item.changeType}</span></header><div className="revision-before-after"><div><small>Đang công khai</small><Value value={item.currentValue} /></div><div><small>Đề xuất</small><Value value={item.proposedValue} /></div></div></article>))}
        </div>
        <label className="form-field">Lý do nếu từ chối<textarea value={rejectReason} onChange={event => setRejectReason(event.target.value)} rows={3} placeholder="Nêu rõ nội dung Staff cần chỉnh sửa" /></label>
        <div className="revision-actions"><button className="button-primary" disabled={busy || !selected.canApprove} onClick={() => void decide('approve')}>Duyệt thay đổi</button><button className="button-secondary" disabled={busy} onClick={() => void decide('reject')}>Từ chối</button></div>
      </>}
    </section>
  </div>
}
