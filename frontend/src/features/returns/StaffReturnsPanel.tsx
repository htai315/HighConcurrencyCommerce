import { useCallback, useEffect, useState } from 'react'
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap'
import { formatPrice } from '../catalog/catalog.api'
import {
  getApprovedReturnsQueue,
  getStaffReturnById,
  inspectReturn,
  receiveReturn,
  returnReasonLabels,
  type StaffOrderReturnSummary,
  type AdminOrderReturnDetail,
} from './returns.api'

type InspectionAllocation = { returnLineId: string; restockQty: number; quarantineQty: number; damagedQty: number; scrappedQty: number }

function formatDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('vi-VN') : '—'
}

export function StaffReturnsPanel() {
  const [items, setItems] = useState<StaffOrderReturnSummary[]>([])
  const [totalCount, setTotalCount] = useState(0)
  const [page, setPage] = useState(1)
  const [searchQuery, setSearchQuery] = useState('')
  const [appliedSearchQuery, setAppliedSearchQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const [receivingItem, setReceivingItem] = useState<StaffOrderReturnSummary | null>(null)
  const [receivedEvidence, setReceivedEvidence] = useState('')
  const [busy, setBusy] = useState(false)
  const [inspectionItem, setInspectionItem] = useState<AdminOrderReturnDetail | null>(null)
  const [inspectionLines, setInspectionLines] = useState<InspectionAllocation[]>([])
  const [inspectionNote, setInspectionNote] = useState('')
  const modalOpen = Boolean(receivingItem || inspectionItem)
  const closeOpenModal = () => {
    setReceivingItem(null)
    setInspectionItem(null)
  }
  const modalRef = useModalFocusTrap<HTMLDivElement>(modalOpen, closeOpenModal, busy)

  const loadQueue = useCallback(async () => {
    try {
      setError(null)
      const data = await getApprovedReturnsQueue({
        page,
        pageSize: 20,
        q: appliedSearchQuery || undefined,
      })
      setItems(data.items)
      setTotalCount(data.totalCount)
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Không thể tải hàng đợi nhận hàng trả.')
    } finally {
      setLoading(false)
    }
  }, [appliedSearchQuery, page])

  useEffect(() => {
    const timer = window.setTimeout(() => void loadQueue(), 0)
    return () => window.clearTimeout(timer)
  }, [loadQueue])

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault()
    const nextQuery = searchQuery.trim()
    setLoading(true)
    setPage(1)
    if (page === 1 && nextQuery === appliedSearchQuery) await loadQueue()
    else setAppliedSearchQuery(nextQuery)
  }

  async function handleReceiveSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!receivingItem) return
    if (!receivedEvidence.trim()) {
      setMessage({ kind: 'error', text: 'Vui lòng nhập ghi chú hoặc biên bản kiểm tra nhận hàng.' })
      return
    }
    setBusy(true)
    try {
      await receiveReturn(receivingItem.id, receivedEvidence.trim())
      setItems(prev => prev.map(item => item.id === receivingItem.id ? { ...item, returnStatus: 'RECEIVED' } : item))
      setMessage({
        kind: 'success',
        text: receivingItem.resolutionType === 'EXCHANGE'
          ? `Đã nhận hàng đơn ${receivingItem.orderNumber} vào khu cách ly. Hãy kiểm định để tạo kiện thay thế.`
          : `Đã nhận hàng đơn ${receivingItem.orderNumber} vào khu cách ly. Hãy kiểm định trước khi hoàn tiền.`,
      })
      setReceivingItem(null)
      setReceivedEvidence('')
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Xác nhận nhận hàng thất bại.' })
    } finally {
      setBusy(false)
    }
  }

  async function openInspection(item: StaffOrderReturnSummary) {
    setBusy(true)
    try {
      const detail = await getStaffReturnById(item.id)
      setInspectionItem(detail)
      setInspectionLines(detail.lines.map(line => ({
        returnLineId: line.id,
        restockQty: line.requestedQty,
        quarantineQty: 0,
        damagedQty: 0,
        scrappedQty: 0,
      })))
      setInspectionNote('')
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Không thể tải chi tiết kiểm định.' })
    } finally {
      setBusy(false)
    }
  }

  function updateAllocation(lineId: string, field: keyof Omit<InspectionAllocation, 'returnLineId'>, rawValue: string) {
    const value = Math.max(0, Number.parseInt(rawValue || '0', 10) || 0)
    setInspectionLines(current => current.map(line => line.returnLineId === lineId ? { ...line, [field]: value } : line))
  }

  async function handleInspectionSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!inspectionItem) return
    const invalidLine = inspectionItem.lines.find(line => {
      const allocation = inspectionLines.find(item => item.returnLineId === line.id)
      return !allocation || allocation.restockQty + allocation.quarantineQty + allocation.damagedQty + allocation.scrappedQty !== line.requestedQty
    })
    if (invalidLine) {
      setMessage({ kind: 'error', text: `Tổng phân loại SKU ${invalidLine.skuCode} phải bằng ${invalidLine.requestedQty}.` })
      return
    }

    setBusy(true)
    try {
      await inspectReturn(inspectionItem.id, { lines: inspectionLines, inspectionNote: inspectionNote.trim() || null })
      setItems(current => current.filter(item => item.id !== inspectionItem.id))
      setTotalCount(current => Math.max(0, current - 1))
      setInspectionItem(null)
      setMessage({ kind: 'success', text: inspectionItem.resolutionType === 'EXCHANGE'
        ? `Đã kiểm định đơn ${inspectionItem.orderNumber}. Kiện sản phẩm thay thế đã sẵn sàng để đóng gói.`
        : `Đã kiểm định hàng trả của đơn ${inspectionItem.orderNumber}. Admin có thể thực hiện hoàn tiền.` })
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Kiểm định hàng trả thất bại.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="staff-returns-panel" style={{ padding: '16px 0' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <div>
          <h2 style={{ margin: 0, fontSize: 18 }}>Tiếp nhận & kiểm định hàng trả</h2>
          <span style={{ fontSize: 13, color: 'var(--muted)' }}>
            Có {totalCount} yêu cầu đang chờ nhận hoặc phân loại chất lượng
          </span>
        </div>
      </div>

      {message && (
        <div className={`alert ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'} style={{ marginBottom: 16 }}>
          {message.text}
        </div>
      )}

      {error && (
        <div className="alert error" role="alert" style={{ marginBottom: 16 }}>
          {error}
        </div>
      )}

      <form onSubmit={handleSearch} style={{ display: 'flex', gap: 8, marginBottom: 16 }}>
        <input
          type="search"
          aria-label="Tìm kiếm hàng hoàn theo mã đơn"
          value={searchQuery}
          onChange={e => setSearchQuery(e.target.value)}
          placeholder="Tìm theo mã đơn hàng..."
          style={{ maxWidth: 300, padding: '6px 10px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
        />
        <button type="submit" className="button secondary" disabled={loading}>
          Tìm
        </button>
      </form>

      {loading ? (
        <p role="status">Đang tải hàng đợi...</p>
      ) : items.length === 0 ? (
        <div style={{ textAlign: 'center', padding: 32, background: 'var(--paper-subtle, #f8fafc)', borderRadius: 8 }}>
          <p style={{ margin: 0, color: 'var(--muted)' }}>Không có hàng hoàn nào đang chờ tiếp nhận.</p>
        </div>
      ) : (
        <div style={{ display: 'grid', gap: 12 }}>
          {items.map(item => (
            <div
              key={item.id}
              className="staff-return-card"
              style={{
                border: '1px solid var(--line, #e2e8f0)',
                borderRadius: 8,
                padding: 14,
                background: '#ffffff',
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                flexWrap: 'wrap',
                gap: 12,
              }}
            >
              <div>
                <strong style={{ fontSize: 15, display: 'block' }}>Đơn hàng #{item.orderNumber}</strong>
                <span style={{ fontSize: 13, color: 'var(--muted)' }}>
                  <strong>{item.resolutionType === 'EXCHANGE' ? 'Đổi SKU' : 'Hoàn tiền'}</strong> · Lý do: <strong>{returnReasonLabels[item.reasonCode] ?? item.reasonCode}</strong> · SL: {item.totalItemCount} sản phẩm
                </span>
                <span style={{ display: 'block', fontSize: 12, color: 'var(--muted)', marginTop: 2 }}>
                  Duyệt lúc: {formatDate(item.decidedAt)}{item.decisionNote ? ` · Ghi chú: ${item.decisionNote}` : ''}
                </span>
              </div>

              <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
                <strong style={{ fontSize: 15, color: 'var(--brand)' }}>{formatPrice(item.refundAmount)}</strong>
                {item.returnStatus === 'APPROVED' ? (
                  <button type="button" className="button primary" style={{ padding: '6px 12px', fontSize: 13 }} onClick={() => setReceivingItem(item)}>
                    Nhận vào khu cách ly
                  </button>
                ) : (
                  <button type="button" className="button primary" style={{ padding: '6px 12px', fontSize: 13 }} onClick={() => void openInspection(item)} disabled={busy}>
                    Kiểm định chất lượng
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Receive Modal */}
      {receivingItem && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="receive-return-title" style={{ maxWidth: 460 }}>
            <h3 id="receive-return-title">Xác nhận nhận hàng hoàn · Đơn {receivingItem.orderNumber}</h3>
            <p>
              Hệ thống chỉ tăng tồn vật lý và đưa hàng vào khu cách ly. Hàng chưa thể bán cho tới khi hoàn tất kiểm định.
            </p>
            <form onSubmit={handleReceiveSubmit}>
              <div style={{ marginBottom: 16 }}>
                <label htmlFor="receive-evidence" style={{ display: 'block', fontWeight: 600, marginBottom: 4 }}>
                  Biên bản / Ghi chú kiểm tra hàng *
                </label>
                <textarea
                  id="receive-evidence"
                  value={receivedEvidence}
                  onChange={e => setReceivedEvidence(e.target.value)}
                  required
                  rows={3}
                  placeholder="Ví dụ: Đã nhận đủ hàng, nguyên tem mác, số biên bản KT-001..."
                  style={{ width: '100%', padding: '6px 8px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
                />
              </div>
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
                <button type="button" className="button secondary" onClick={() => setReceivingItem(null)} disabled={busy}>
                  Hủy bỏ
                </button>
                <button type="submit" className="button primary" disabled={busy}>
                  {busy ? 'Đang nhận hàng...' : 'Xác nhận nhận vào cách ly'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {inspectionItem && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-label="Kiểm định hàng trả" style={{ maxWidth: 760 }}>
            <h3 id="inspect-return-title">Kiểm định hàng trả · Đơn {inspectionItem.orderNumber}</h3>
            <p>Phân loại đủ số lượng của từng SKU. Hàng “Cách ly” vẫn không khả dụng; hàng “Loại bỏ” sẽ giảm tồn vật lý.</p>
            <form onSubmit={handleInspectionSubmit}>
              <div style={{ display: 'grid', gap: 12 }}>
                {inspectionItem.lines.map(line => {
                  const allocation = inspectionLines.find(item => item.returnLineId === line.id)!
                  const total = allocation ? allocation.restockQty + allocation.quarantineQty + allocation.damagedQty + allocation.scrappedQty : 0
                  return (
                    <fieldset key={line.id} style={{ border: '1px solid var(--line)', borderRadius: 8, padding: 12 }}>
                      <legend><strong>{line.skuCode}</strong> · {line.optionLabel} · cần phân loại {line.requestedQty}</legend>
                      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(110px, 1fr))', gap: 10 }}>
                        {([
                          ['restockQty', 'Bán lại'], ['quarantineQty', 'Cách ly'], ['damagedQty', 'Hư hỏng'], ['scrappedQty', 'Loại bỏ'],
                        ] as const).map(([field, label]) => (
                          <label key={field}>{label}
                            <input type="number" min={0} max={line.requestedQty} value={allocation?.[field] ?? 0} onChange={event => updateAllocation(line.id, field, event.target.value)} style={{ width: '100%' }} />
                          </label>
                        ))}
                      </div>
                      <small style={{ color: total === line.requestedQty ? '#047857' : '#b91c1c' }}>Đã phân loại: {total}/{line.requestedQty}</small>
                    </fieldset>
                  )
                })}
              </div>
              <label style={{ display: 'block', marginTop: 14 }}>Ghi chú kiểm định
                <textarea value={inspectionNote} onChange={event => setInspectionNote(event.target.value)} maxLength={500} rows={3} style={{ width: '100%' }} />
              </label>
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10, marginTop: 16 }}>
                <button type="button" className="button secondary" onClick={() => setInspectionItem(null)} disabled={busy}>Đóng</button>
                <button type="submit" className="button primary" disabled={busy}>{busy ? 'Đang lưu...' : 'Hoàn tất kiểm định'}</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}

