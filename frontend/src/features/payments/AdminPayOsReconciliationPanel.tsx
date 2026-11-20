import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { formatPrice } from '../catalog/catalog.api'
import {
  claimReconciliationCase, getReconciliationCase, getReconciliationCases,
  refundReconciliationCase, resolveReconciliationCase,
  type ReconciliationDetail, type ReconciliationSummary,
} from './reconciliation.api'

const reasons: Record<string, string> = {
  LATE_PAID: 'Thanh toán đến muộn', AMOUNT_MISMATCH: 'Sai lệch số tiền',
  DUPLICATE_COLLECTION: 'Thu tiền trùng', OTHER: 'Khác',
}

export function AdminPayOsReconciliationPanel() {
  const [items, setItems] = useState<ReconciliationSummary[]>([])
  const [selected, setSelected] = useState<ReconciliationDetail | null>(null)
  const [status, setStatus] = useState('')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')
  const [evidence, setEvidence] = useState('')
  const [confirmingRefund, setConfirmingRefund] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try { setItems((await getReconciliationCases(status)).items) }
    catch { setMessage({ kind: 'error', text: 'Không thể tải hàng đợi đối soát PayOS.' }) }
    finally { setLoading(false) }
  }, [status])
  useEffect(() => { const timer = window.setTimeout(() => void refresh(), 0); return () => window.clearTimeout(timer) }, [refresh])

  async function open(item: ReconciliationSummary) {
    setBusy(true); setMessage(null)
    try { setSelected(await getReconciliationCase(item.id)); setNote(''); setEvidence(''); setConfirmingRefund(false) }
    catch { setMessage({ kind: 'error', text: 'Không thể tải chi tiết case.' }) }
    finally { setBusy(false) }
  }
  async function action(run: () => Promise<ReconciliationDetail>, success: string) {
    setBusy(true); setMessage(null)
    try { setSelected(await run()); setMessage({ kind: 'success', text: success }); await refresh() }
    catch (error) { setMessage({ kind: 'error', text: error instanceof Error ? error.message : 'Thao tác không thành công.' }) }
    finally { setBusy(false) }
  }

  return <section className="management-card payos-reconciliation-panel">
    <div className="cod-queue-header">
      <div><h2>Đối soát PayOS ({items.length})</h2><p>Case tiền đến muộn hoặc sai lệch phải được nhận xử lý và có Audit Log trước khi đóng.</p></div>
      <button type="button" className="button-secondary" disabled={loading || busy} onClick={() => void refresh()}>Làm mới</button>
    </div>
    <label className="workspace-search">Trạng thái
      <select value={status} onChange={event => setStatus(event.target.value)}><option value="">Tất cả</option><option value="OPEN">Chờ xử lý</option><option value="IN_REVIEW">Đang xử lý</option><option value="RESOLVED">Đã đóng</option></select>
    </label>
    {message && <p role={message.kind === 'error' ? 'alert' : 'status'} className={`alert ${message.kind}`}>{message.text}</p>}
    {loading ? <p role="status">Đang tải đối soát…</p> : items.length === 0 ? <div className="empty-state compact"><h3>Không có case phù hợp</h3></div> :
      <div className="table-responsive"><table className="admin-table"><thead><tr><th>Đơn hàng</th><th>Khách hàng</th><th>Lý do</th><th>Thanh toán</th><th>Trạng thái</th><th></th></tr></thead><tbody>
        {items.map(item => <tr key={item.id}><td><strong>{item.orderNumber}</strong><br /><small>{new Date(item.createdAt).toLocaleString('vi-VN')}</small></td><td>{item.customerEmail}</td><td>{reasons[item.reason] ?? item.reason}</td><td>{formatPrice(item.expectedAmount)}<br /><small>{item.paymentStatus}</small></td><td><span className={`badge badge-${item.caseStatus.toLowerCase()}`}>{item.caseStatus}</span>{item.assignedToName && <small className="block">{item.assignedToName}</small>}</td><td><button type="button" className="button-secondary" disabled={busy} onClick={() => void open(item)}>Chi tiết</button></td></tr>)}
      </tbody></table></div>}

    {selected && <div className="reconciliation-detail" role="region" aria-label={`Case ${selected.orderNumber}`}>
      <div className="cod-queue-header"><div><p className="eyebrow">{reasons[selected.reason] ?? selected.reason}</p><h3>{selected.orderNumber} · {selected.customerName}</h3><p>{selected.customerEmail}</p></div><button type="button" className="text-button" onClick={() => setSelected(null)}>Đóng</button></div>
      <div className="payos-status-grid"><div><span>Case</span><strong>{selected.caseStatus}</strong></div><div><span>Payment</span><strong>{selected.paymentStatus}</strong></div><div><span>Kỳ vọng</span><strong>{formatPrice(selected.expectedAmount)}</strong></div><div><span>Phụ trách</span><strong>{selected.assignedToName ?? 'Chưa nhận'}</strong></div></div>
      <h4>Giao dịch đã sanitize</h4>
      <ul className="reconciliation-transactions">{selected.transactions.map(tx => <li key={tx.id}><span>{tx.providerTransactionId ?? 'Không có reference'}</span><strong>{formatPrice(tx.amount)}</strong><span>{tx.transactionStatus}</span></li>)}</ul>
      {selected.manualRefund && <p className="alert success">Đã hoàn {formatPrice(selected.manualRefund.amount)} · Chứng từ {selected.manualRefund.evidenceReference}</p>}
      {selected.caseStatus === 'OPEN' && <button type="button" className="button-primary" disabled={busy} onClick={() => void action(() => claimReconciliationCase(selected.id), 'Đã nhận xử lý case.')}>Nhận xử lý</button>}
      {selected.caseStatus === 'IN_REVIEW' && <div className="reconciliation-actions">
        <button type="button" className="text-button" disabled={busy} onClick={() => void action(() => claimReconciliationCase(selected.id), 'Bạn đang phụ trách case này.')}>Nhận lại / tiếp quản case hết lease</button>
        <label>Ghi chú quyết định<textarea value={note} maxLength={1000} onChange={event => setNote(event.target.value)} placeholder="Tối thiểu 5 ký tự" /></label>
        <label>Mã chứng từ hoàn tiền<input value={evidence} maxLength={100} onChange={event => setEvidence(event.target.value)} placeholder="Chỉ nhập khi đã hoàn tiền bên ngoài" /></label>
        <div>{!['LATE_PAID', 'DUPLICATE_COLLECTION'].includes(selected.reason) && <button type="button" className="button-secondary" disabled={busy || note.trim().length < 5} onClick={() => void action(() => resolveReconciliationCase(selected.id, note.trim()), 'Đã đóng case không hoàn tiền.')}>Đóng case</button>}
        <button type="button" className="button-primary" disabled={busy || note.trim().length < 5 || evidence.trim().length < 3} onClick={() => setConfirmingRefund(true)}>Ghi nhận hoàn tiền</button></div>
      </div>}
      {selected.note && <p><strong>Kết luận:</strong> {selected.note}</p>}
    </div>}
    {confirmingRefund && selected && <ConfirmDialog
      title={`Xác nhận hoàn tiền cho ${selected.orderNumber}?`}
      description={`Chứng từ ${evidence.trim()} · Số tiền lấy từ giao dịch PayOS đã xác minh. Thao tác này sẽ được ghi Audit Log và không thể hoàn tác trong ứng dụng.`}
      confirmLabel="Xác nhận đã hoàn tiền"
      busy={busy}
      onCancel={() => setConfirmingRefund(false)}
      onConfirm={() => void action(
        () => refundReconciliationCase(selected.id, evidence.trim(), note.trim()),
        'Đã ghi nhận hoàn tiền thủ công.',
      ).finally(() => setConfirmingRefund(false))}
    />}
  </section>
}
