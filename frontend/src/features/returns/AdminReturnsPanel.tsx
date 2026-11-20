import { useCallback, useEffect, useState } from 'react'
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap'
import { formatPrice } from '../catalog/catalog.api'
import {
  approveReturn,
  getAdminReturnById,
  getAdminReturns,
  processRefund,
  rejectReturn,
  returnReasonLabels,
  returnStatusLabels,
  type AdminOrderReturnDetail,
  type AdminOrderReturnSummary,
} from './returns.api'

function formatDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('vi-VN') : '—'
}

export function AdminReturnsPanel() {
  const [items, setItems] = useState<AdminOrderReturnSummary[]>([])
  const [totalCount, setTotalCount] = useState(0)
  const [page, setPage] = useState(1)
  const [statusFilter, setStatusFilter] = useState<string>('')
  const [searchQuery, setSearchQuery] = useState('')
  const [appliedSearchQuery, setAppliedSearchQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  // Actions state
  const [selectedDetail, setSelectedDetail] = useState<AdminOrderReturnDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)

  const [approvingItem, setApprovingItem] = useState<AdminOrderReturnSummary | null>(null)
  const [approveNote, setApproveNote] = useState('')

  const [rejectingItem, setRejectingItem] = useState<AdminOrderReturnSummary | null>(null)
  const [rejectNote, setRejectNote] = useState('')

  const [refundingItem, setRefundingItem] = useState<AdminOrderReturnSummary | null>(null)
  const [evidenceReference, setEvidenceReference] = useState('')

  const [actionBusy, setActionBusy] = useState(false)
  const modalOpen = Boolean(approvingItem || rejectingItem || refundingItem || selectedDetail)
  const closeOpenModal = () => {
    setApprovingItem(null)
    setRejectingItem(null)
    setRefundingItem(null)
    setSelectedDetail(null)
  }
  const modalRef = useModalFocusTrap<HTMLDivElement>(modalOpen, closeOpenModal, actionBusy)

  const loadData = useCallback(async () => {
    try {
      setError(null)
      const data = await getAdminReturns({
        page,
        pageSize: 20,
        status: statusFilter || undefined,
        q: appliedSearchQuery || undefined,
      })
      setItems(data.items)
      setTotalCount(data.totalCount)
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Không thể tải danh sách đổi/trả.')
    } finally {
      setLoading(false)
    }
  }, [appliedSearchQuery, page, statusFilter])

  useEffect(() => {
    const timer = window.setTimeout(() => void loadData(), 0)
    return () => window.clearTimeout(timer)
  }, [loadData])

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault()
    const nextQuery = searchQuery.trim()
    setLoading(true)
    setPage(1)
    if (page === 1 && nextQuery === appliedSearchQuery) await loadData()
    else setAppliedSearchQuery(nextQuery)
  }

  async function handleViewDetail(returnId: string) {
    try {
      setDetailLoading(true)
      const detail = await getAdminReturnById(returnId)
      setSelectedDetail(detail)
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Không thể xem chi tiết.' })
    } finally {
      setDetailLoading(false)
    }
  }

  async function handleApproveSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!approvingItem) return
    setActionBusy(true)
    try {
      const updated = await approveReturn(approvingItem.id, approveNote.trim() || undefined)
      setItems(prev => prev.map(i => (i.id === updated.id ? { ...i, returnStatus: updated.returnStatus } : i)))
      setMessage({ kind: 'success', text: `Đã phê duyệt yêu cầu cho đơn ${approvingItem.orderNumber}.` })
      setApprovingItem(null)
      setApproveNote('')
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Phê duyệt thất bại.' })
    } finally {
      setActionBusy(false)
    }
  }

  async function handleRejectSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!rejectingItem) return
    if (!rejectNote.trim() || rejectNote.trim().length < 3) {
      setMessage({ kind: 'error', text: 'Lý do từ chối cần ít nhất 3 ký tự.' })
      return
    }
    setActionBusy(true)
    try {
      const updated = await rejectReturn(rejectingItem.id, rejectNote.trim())
      setItems(prev => prev.map(i => (i.id === updated.id ? { ...i, returnStatus: updated.returnStatus } : i)))
      setMessage({ kind: 'success', text: `Đã từ chối yêu cầu cho đơn ${rejectingItem.orderNumber}.` })
      setRejectingItem(null)
      setRejectNote('')
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Từ chối thất bại.' })
    } finally {
      setActionBusy(false)
    }
  }

  async function handleRefundSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!refundingItem) return
    if (!evidenceReference.trim()) {
      setMessage({ kind: 'error', text: 'Mã chứng từ hoàn tiền không được để trống.' })
      return
    }
    setActionBusy(true)
    try {
      const updated = await processRefund(refundingItem.id, evidenceReference.trim())
      setItems(prev => prev.map(i => (i.id === updated.id ? { ...i, returnStatus: updated.returnStatus } : i)))
      setMessage({ kind: 'success', text: `Đã hoàn tiền cho yêu cầu trả hàng đơn ${refundingItem.orderNumber}.` })
      setRefundingItem(null)
      setEvidenceReference('')
    } catch (err: unknown) {
      setMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Xác nhận hoàn tiền thất bại.' })
    } finally {
      setActionBusy(false)
    }
  }

  return (
    <div className="admin-returns-panel" style={{ padding: '16px 0' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <div>
          <h2 style={{ margin: 0, fontSize: 18 }}>Quản lý Đổi/Trả & Hoàn tiền</h2>
          <span style={{ fontSize: 13, color: 'var(--muted)' }}>Tổng cộng {totalCount} yêu cầu đổi/trả hàng</span>
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

      {/* Filter & Search Bar */}
      <div
        style={{
          display: 'flex',
          gap: 12,
          flexWrap: 'wrap',
          marginBottom: 16,
          padding: 12,
          background: 'var(--paper-subtle, #f8fafc)',
          borderRadius: 8,
        }}
      >
        <form onSubmit={handleSearch} style={{ display: 'flex', gap: 8, flex: 1 }}>
          <input
            type="search"
            aria-label="Tìm kiếm yêu cầu đổi trả"
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
            placeholder="Tìm theo mã đơn, email hoặc tên khách hàng..."
            style={{ flex: 1, padding: '6px 10px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
          />
          <button type="submit" className="button secondary" disabled={loading}>
            Tìm kiếm
          </button>
        </form>

        <select
          aria-label="Lọc yêu cầu theo trạng thái"
          value={statusFilter}
          onChange={e => {
            setLoading(true)
            setStatusFilter(e.target.value)
            setPage(1)
          }}
          style={{ padding: '6px 10px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
        >
          <option value="">Tất cả trạng thái</option>
          {Object.entries(returnStatusLabels).map(([code, lbl]) => (
            <option key={code} value={code}>
              {lbl}
            </option>
          ))}
        </select>
      </div>

      {/* Table */}
      {loading ? (
        <p role="status">Đang tải danh sách đổi/trả...</p>
      ) : items.length === 0 ? (
        <p style={{ textAlign: 'center', padding: 32, color: 'var(--muted)' }}>Không có yêu cầu đổi/trả nào phù hợp.</p>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="admin-table" style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
            <thead>
              <tr style={{ borderBottom: '2px solid var(--line, #cbd5e1)', textAlign: 'left' }}>
                <th style={{ padding: '8px 10px' }}>Mã đơn</th>
                <th style={{ padding: '8px 10px' }}>Khách hàng</th>
                <th style={{ padding: '8px 10px' }}>Lý do</th>
                <th style={{ padding: '8px 10px' }}>Hình thức</th>
                <th style={{ padding: '8px 10px' }}>Tiền hoàn</th>
                <th style={{ padding: '8px 10px' }}>Trạng thái</th>
                <th style={{ padding: '8px 10px' }}>Thời gian</th>
                <th style={{ padding: '8px 10px', textAlign: 'right' }}>Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {items.map(item => (
                <tr key={item.id} style={{ borderBottom: '1px solid var(--line, #e2e8f0)' }}>
                  <td style={{ padding: '8px 10px', fontWeight: 600 }}>{item.orderNumber}</td>
                  <td style={{ padding: '8px 10px' }}>
                    <div>{item.customerFullName}</div>
                    <span style={{ fontSize: 12, color: 'var(--muted)' }}>{item.customerEmail}</span>
                  </td>
                  <td style={{ padding: '8px 10px' }}>{returnReasonLabels[item.reasonCode] ?? item.reasonCode}</td>
                  <td style={{ padding: '8px 10px' }}><span className="resolution-badge">{item.resolutionType === 'EXCHANGE' ? 'Đổi SKU' : 'Hoàn tiền'}</span></td>
                  <td style={{ padding: '8px 10px', fontWeight: 600, color: 'var(--brand)' }}>
                    {item.resolutionType === 'EXCHANGE' ? 'Không chênh lệch' : formatPrice(item.refundAmount)}
                  </td>
                  <td style={{ padding: '8px 10px' }}>
                    <span
                      style={{
                        padding: '2px 8px',
                        borderRadius: 4,
                        fontSize: 12,
                        fontWeight: 600,
                        background:
                          item.returnStatus === 'REFUNDED'
                            ? '#dcfce7'
                            : item.returnStatus === 'APPROVED'
                            ? '#dbeafe'
                            : item.returnStatus === 'RECEIVED' || item.returnStatus === 'INSPECTED'
                            ? '#fef3c7'
                            : item.returnStatus === 'REJECTED' || item.returnStatus === 'CANCELLED'
                            ? '#f1f5f9'
                            : '#fef08a',
                        color:
                          item.returnStatus === 'REFUNDED'
                            ? '#15803d'
                            : item.returnStatus === 'APPROVED'
                            ? '#1d4ed8'
                            : item.returnStatus === 'RECEIVED' || item.returnStatus === 'INSPECTED'
                            ? '#b45309'
                            : item.returnStatus === 'REJECTED' || item.returnStatus === 'CANCELLED'
                            ? '#64748b'
                            : '#854d0e',
                      }}
                    >
                      {returnStatusLabels[item.returnStatus] ?? item.returnStatus}
                    </span>
                  </td>
                  <td style={{ padding: '8px 10px', fontSize: 12, color: 'var(--muted)' }}>
                    {formatDate(item.requestedAt)}
                  </td>
                  <td style={{ padding: '8px 10px', textAlign: 'right' }}>
                    <div style={{ display: 'inline-flex', gap: 6 }}>
                      <button
                        type="button"
                        className="button secondary"
                        style={{ padding: '3px 8px', fontSize: 12 }}
                        onClick={() => void handleViewDetail(item.id)}
                        disabled={detailLoading}
                      >
                        Chi tiết
                      </button>
                      {item.returnStatus === 'REQUESTED' && (
                        <>
                          <button
                            type="button"
                            className="button primary"
                            style={{ padding: '3px 8px', fontSize: 12 }}
                            onClick={() => setApprovingItem(item)}
                          >
                            Duyệt
                          </button>
                          <button
                            type="button"
                            className="button secondary"
                            style={{ padding: '3px 8px', fontSize: 12, color: 'var(--danger-ink, #dc2626)' }}
                            onClick={() => setRejectingItem(item)}
                          >
                            Từ chối
                          </button>
                        </>
                      )}
                      {item.returnStatus === 'INSPECTED' && (
                        <button
                          type="button"
                          className="button primary"
                          style={{ padding: '3px 8px', fontSize: 12 }}
                          onClick={() => setRefundingItem(item)}
                        >
                          Hoàn tiền
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Approve Modal */}
      {approvingItem && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="approve-return-title" style={{ maxWidth: 450 }}>
            <h3 id="approve-return-title">Phê duyệt yêu cầu {approvingItem.resolutionType === 'EXCHANGE' ? 'đổi sản phẩm' : 'trả hàng'} · Đơn {approvingItem.orderNumber}</h3>
            {approvingItem.resolutionType === 'EXCHANGE' ? (
              <div className="alert info">Duyệt sẽ giữ tồn SKU thay thế trong 7 ngày. Giao dịch gốc không bị hoàn tiền hay thay đổi voucher.</div>
            ) : <p>Số tiền dự kiến hoàn trả: <strong>{formatPrice(approvingItem.refundAmount)}</strong></p>}
            <form onSubmit={handleApproveSubmit}>
              <div style={{ marginBottom: 16 }}>
                <label htmlFor="approve-note" style={{ display: 'block', fontWeight: 600, marginBottom: 4 }}>
                  Ghi chú duyệt (không bắt buộc)
                </label>
                <textarea
                  id="approve-note"
                  value={approveNote}
                  onChange={e => setApproveNote(e.target.value)}
                  rows={2}
                  style={{ width: '100%', padding: '6px 8px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
                  placeholder="Ghi chú hướng dẫn cho nhân viên tiếp nhận hàng..."
                />
              </div>
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
                <button type="button" className="button secondary" onClick={() => setApprovingItem(null)} disabled={actionBusy}>
                  Đóng
                </button>
                <button type="submit" className="button primary" disabled={actionBusy}>
                  {actionBusy ? 'Đang duyệt...' : 'Xác nhận duyệt'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Reject Modal */}
      {rejectingItem && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="reject-return-title" style={{ maxWidth: 450 }}>
            <h3 id="reject-return-title">Từ chối yêu cầu trả hàng · Đơn {rejectingItem.orderNumber}</h3>
            <form onSubmit={handleRejectSubmit}>
              <div style={{ marginBottom: 16 }}>
                <label htmlFor="reject-note" style={{ display: 'block', fontWeight: 600, marginBottom: 4 }}>
                  Lý do từ chối *
                </label>
                <textarea
                  id="reject-note"
                  value={rejectNote}
                  onChange={e => setRejectNote(e.target.value)}
                  rows={3}
                  required
                  style={{ width: '100%', padding: '6px 8px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
                  placeholder="Nêu rõ lý do từ chối để thông báo cho khách hàng..."
                />
              </div>
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
                <button type="button" className="button secondary" onClick={() => setRejectingItem(null)} disabled={actionBusy}>
                  Đóng
                </button>
                <button type="submit" className="button secondary" style={{ color: 'var(--danger-ink, #dc2626)' }} disabled={actionBusy}>
                  {actionBusy ? 'Đang xử lý...' : 'Xác nhận từ chối'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Refund Modal */}
      {refundingItem && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="refund-return-title" style={{ maxWidth: 450 }}>
            <h3 id="refund-return-title">Xác nhận hoàn tiền thủ công · Đơn {refundingItem.orderNumber}</h3>
            <p>
              Số tiền cần hoàn: <strong style={{ color: 'var(--brand)' }}>{formatPrice(refundingItem.refundAmount)}</strong>
            </p>
            <form onSubmit={handleRefundSubmit}>
              <div style={{ marginBottom: 16 }}>
                <label htmlFor="evidence-reference" style={{ display: 'block', fontWeight: 600, marginBottom: 4 }}>
                  Mã chứng từ hoàn tiền *
                </label>
                <input
                  id="evidence-reference"
                  value={evidenceReference}
                  onChange={e => setEvidenceReference(e.target.value)}
                  required
                  placeholder="Ví dụ: FT260012345678, PHIEUCHI-001..."
                  style={{ width: '100%', padding: '6px 8px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
                />
                <small style={{ color: 'var(--muted)', display: 'block', marginTop: 4 }}>
                  Mã giao dịch ngân hàng hoặc số phiếu chi tiền mặt.
                </small>
              </div>
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
                <button type="button" className="button secondary" onClick={() => setRefundingItem(null)} disabled={actionBusy}>
                  Hủy bỏ
                </button>
                <button type="submit" className="button primary" disabled={actionBusy}>
                  {actionBusy ? 'Đang hoàn tiền...' : 'Xác nhận đã hoàn tiền'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Detail Modal */}
      {selectedDetail && (
        <div className="confirm-overlay" role="presentation">
          <div ref={modalRef} tabIndex={-1} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="return-detail-title" style={{ maxWidth: 640, width: '90%' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
              <h3 id="return-detail-title" style={{ margin: 0 }}>Chi tiết yêu cầu đổi/trả · Đơn {selectedDetail.orderNumber}</h3>
              <button type="button" className="text-button" aria-label="Đóng chi tiết yêu cầu đổi trả" onClick={() => setSelectedDetail(null)}>✕</button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, fontSize: 13, marginBottom: 16 }}>
              <div>Khách hàng: <strong>{selectedDetail.customerFullName}</strong> ({selectedDetail.customerEmail})</div>
              <div>Trạng thái: <strong>{returnStatusLabels[selectedDetail.returnStatus] ?? selectedDetail.returnStatus}</strong></div>
              <div>Hình thức: <strong>{selectedDetail.resolutionType === 'EXCHANGE' ? 'Đổi SKU trực tiếp' : 'Trả hàng & hoàn tiền'}</strong></div>
              <div>Lý do: <strong>{returnReasonLabels[selectedDetail.reasonCode] ?? selectedDetail.reasonCode}</strong></div>
              <div>Tổng tiền hoàn: <strong style={{ color: 'var(--brand)' }}>{formatPrice(selectedDetail.refundAmount)}</strong></div>
              {selectedDetail.customerNote && <div style={{ gridColumn: 'span 2' }}>Ghi chú khách: <em>{selectedDetail.customerNote}</em></div>}
              {selectedDetail.decisionNote && <div style={{ gridColumn: 'span 2' }}>Ghi chú duyệt/từ chối: <em>{selectedDetail.decisionNote}</em></div>}
              {selectedDetail.receivedEvidence && <div style={{ gridColumn: 'span 2' }}>Chứng từ nhận hàng: <em>{selectedDetail.receivedEvidence}</em></div>}
              {selectedDetail.evidenceReference && <div style={{ gridColumn: 'span 2' }}>Mã chứng từ hoàn tiền: <strong>{selectedDetail.evidenceReference}</strong></div>}
            </div>

            <h4 style={{ margin: '0 0 8px', fontSize: 14 }}>Danh sách sản phẩm yêu cầu trả</h4>
            <div style={{ maxHeight: 200, overflowY: 'auto', marginBottom: 16 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--line, #cbd5e1)', textAlign: 'left' }}>
                    <th style={{ padding: '4px 6px' }}>Sản phẩm</th>
                    <th style={{ padding: '4px 6px' }}>SL trả</th>
                    <th style={{ padding: '4px 6px' }}>Đơn giá</th>
                    <th style={{ padding: '4px 6px' }}>Tổng thô</th>
                    <th style={{ padding: '4px 6px' }}>Giảm voucher</th>
                    <th style={{ padding: '4px 6px', textAlign: 'right' }}>Tiền hoàn</th>
                  </tr>
                </thead>
                <tbody>
                  {selectedDetail.lines.map(line => (
                    <tr key={line.id} style={{ borderBottom: '1px solid var(--line, #f1f5f9)' }}>
                      <td style={{ padding: '6px' }}>
                        <strong>{line.productTitle}</strong>
                        <span style={{ display: 'block', fontSize: 11, color: 'var(--muted)' }}>{line.optionLabel}</span>
                        {line.exchange && <span className="exchange-target-label">→ {line.exchange.targetOptionLabel} · {line.exchange.targetSkuCode} ({line.exchange.allocationStatus ?? 'chờ duyệt'})</span>}
                      </td>
                      <td style={{ padding: '6px' }}>{line.requestedQty}</td>
                      <td style={{ padding: '6px' }}>{formatPrice(line.unitPrice)}</td>
                      <td style={{ padding: '6px' }}>{formatPrice(line.grossAmount)}</td>
                      <td style={{ padding: '6px', color: 'var(--muted)' }}>-{formatPrice(line.allocatedVoucherDiscount)}</td>
                      <td style={{ padding: '6px', textAlign: 'right', fontWeight: 600 }}>{formatPrice(line.refundAmount)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div style={{ textAlign: 'right' }}>
              <button type="button" className="button secondary" onClick={() => setSelectedDetail(null)}>Đóng</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

