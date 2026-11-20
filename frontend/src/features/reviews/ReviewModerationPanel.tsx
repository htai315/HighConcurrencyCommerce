import { useState, useEffect } from 'react'
import {
  type ModerationProductReview,
  getModerationReviews,
  moderateReview,
} from './reviews.api'
import { RatingStars } from './RatingStars'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap'

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    if (isNaN(d.getTime())) return iso
    return new Intl.DateTimeFormat('vi-VN', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).format(d)
  } catch {
    return iso
  }
}

export function ReviewModerationPanel() {
  const [statusFilter, setStatusFilter] = useState<'ALL' | 'PUBLISHED' | 'HIDDEN'>('ALL')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const pageSize = 20

  const [items, setItems] = useState<ModerationProductReview[]>([])
  const [totalCount, setTotalCount] = useState(0)
  const [loading, setLoading] = useState(true)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  // Dialog state for HIDE action
  const [hidingTarget, setHidingTarget] = useState<ModerationProductReview | null>(null)
  const [hideReason, setHideReason] = useState('')
  const [hideReasonError, setHideReasonError] = useState<string | null>(null)
  const [submittingAction, setSubmittingAction] = useState(false)

  // Confirmation state for RESTORE action
  const [restoringTarget, setRestoringTarget] = useState<ModerationProductReview | null>(null)
  const [reloadTrigger, setReloadTrigger] = useState(0)
  const closeHideDialog = () => {
    setHidingTarget(null)
    setHideReason('')
    setHideReasonError(null)
  }
  const hideDialogRef = useModalFocusTrap<HTMLDivElement>(Boolean(hidingTarget), closeHideDialog, submittingAction)

  useEffect(() => {
    let ignore = false
    getModerationReviews({
      status: statusFilter,
      search,
      page,
      pageSize,
    })
      .then(res => {
        if (!ignore) {
          setItems(res.items)
          setTotalCount(res.totalCount)
        }
      })
      .catch((err: unknown) => {
        if (!ignore) {
          setMessage({
            type: 'error',
            text: err instanceof Error ? err.message : 'Không tải được danh sách đánh giá kiểm duyệt.',
          })
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoading(false)
        }
      })
    return () => {
      ignore = true
    }
  }, [statusFilter, search, page, reloadTrigger])

  async function handleConfirmHide() {
    if (!hidingTarget) return
    const trimmed = hideReason.trim()
    if (trimmed.length < 3) {
      setHideReasonError('Lý do ẩn đánh giá cần tối thiểu 3 ký tự.')
      return
    }
    if (trimmed.length > 500) {
      setHideReasonError('Lý do ẩn đánh giá không được vượt quá 500 ký tự.')
      return
    }

    setSubmittingAction(true)
    setHideReasonError(null)
    try {
      await moderateReview(hidingTarget.id, {
        action: 'HIDE',
        reason: trimmed,
      })
      setMessage({ type: 'success', text: `Đã ẩn đánh giá của khách hàng "${hidingTarget.customerName}".` })
      setHidingTarget(null)
      setHideReason('')
      setLoading(true)
      setReloadTrigger(k => k + 1)
    } catch (err: unknown) {
      setHideReasonError(err instanceof Error ? err.message : 'Ẩn đánh giá thất bại.')
    } finally {
      setSubmittingAction(false)
    }
  }

  async function handleConfirmRestore() {
    if (!restoringTarget) return
    setSubmittingAction(true)
    try {
      await moderateReview(restoringTarget.id, {
        action: 'RESTORE',
      })
      setMessage({ type: 'success', text: `Đã khôi phục hiển thị đánh giá của "${restoringTarget.customerName}".` })
      setRestoringTarget(null)
      setLoading(true)
      setReloadTrigger(k => k + 1)
    } catch (err: unknown) {
      setMessage({
        type: 'error',
        text: err instanceof Error ? err.message : 'Khôi phục đánh giá thất bại.',
      })
      setRestoringTarget(null)
    } finally {
      setSubmittingAction(false)
    }
  }

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))

  return (
    <div className="review-moderation-panel" aria-label="Kiểm duyệt đánh giá sản phẩm">
      {message && (
        <div className={`alert alert-${message.type === 'success' ? 'success' : 'danger'}`} role="status">
          <span>{message.text}</span>
        </div>
      )}

      {/* Modal Dialog ẩn đánh giá */}
      {hidingTarget && (
        <div ref={hideDialogRef} tabIndex={-1} className="modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="hide-modal-title">
          <div className="modal-content">
            <h3 id="hide-modal-title">Ẩn đánh giá của khách hàng</h3>
            <p>
              Bạn đang thực hiện ẩn đánh giá của <strong>{hidingTarget.customerName}</strong> ({hidingTarget.customerEmail}) cho sản phẩm <em>"{hidingTarget.productTitle}"</em>.
            </p>
            {hideReasonError && (
              <div className="alert alert-danger" role="alert">
                <span>{hideReasonError}</span>
              </div>
            )}
            <div className="form-group">
              <label htmlFor="hide-reason-input" className="form-label">
                Lý do ẩn đánh giá <span className="required-mark">*</span>
              </label>
              <textarea
                id="hide-reason-input"
                className="form-textarea"
                rows={3}
                placeholder="Ví dụ: Ngôn từ không phù hợp / Quảng cáo sai quy định / Vi phạm chính sách cộng đồng..."
                value={hideReason}
                onChange={e => setHideReason(e.target.value)}
                maxLength={500}
                disabled={submittingAction}
              />
              <span className="char-counter">{hideReason.trim().length} / 500 ký tự (tối thiểu 3 ký tự)</span>
            </div>
            <div className="modal-actions">
              <button
                type="button"
                className="button secondary"
                onClick={() => {
                  setHidingTarget(null)
                  setHideReason('')
                  setHideReasonError(null)
                }}
                disabled={submittingAction}
              >
                Hủy
              </button>
              <button
                type="button"
                className="button danger"
                onClick={handleConfirmHide}
                disabled={submittingAction || hideReason.trim().length < 3}
              >
                {submittingAction ? 'Đang xử lý…' : 'Xác nhận ẩn'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Confirmation Dialog khôi phục */}
      {restoringTarget && (
        <ConfirmDialog
          title="Khôi phục đánh giá sản phẩm"
          description={`Bạn có muốn khôi phục hiển thị công khai đánh giá của "${restoringTarget.customerName}" cho sản phẩm "${restoringTarget.productTitle}"?`}
          confirmLabel="Khôi phục"
          busy={submittingAction}
          onCancel={() => setRestoringTarget(null)}
          onConfirm={handleConfirmRestore}
        />
      )}

      {/* Toolbar lọc và tìm kiếm */}
      <div className="moderation-toolbar">
        <div className="moderation-status-tabs" role="group" aria-label="Bộ lọc trạng thái">
          <button
            type="button"
            className={`status-tab-btn ${statusFilter === 'ALL' ? 'active' : ''}`}
            onClick={() => {
              setStatusFilter('ALL')
              setPage(1)
            }}
          >
            Tất cả
          </button>
          <button
            type="button"
            className={`status-tab-btn ${statusFilter === 'PUBLISHED' ? 'active' : ''}`}
            onClick={() => {
              setStatusFilter('PUBLISHED')
              setPage(1)
            }}
          >
            Đang hiển thị
          </button>
          <button
            type="button"
            className={`status-tab-btn ${statusFilter === 'HIDDEN' ? 'active' : ''}`}
            onClick={() => {
              setStatusFilter('HIDDEN')
              setPage(1)
            }}
          >
            Đã ẩn
          </button>
        </div>

        <div className="moderation-search-box">
          <input
            type="search"
            placeholder="Tìm theo sản phẩm, tên khách, email…"
            value={search}
            onChange={e => {
              setSearch(e.target.value)
              setPage(1)
            }}
            aria-label="Tìm kiếm đánh giá"
            className="form-input moderation-search-input"
          />
        </div>
      </div>

      {/* Table view */}
      {loading ? (
        <div className="moderation-loading" aria-live="polite">
          <span>Đang tải danh sách kiểm duyệt…</span>
        </div>
      ) : items.length === 0 ? (
        <div className="moderation-empty">
          <p>Không có đánh giá nào phù hợp với điều kiện tìm kiếm.</p>
        </div>
      ) : (
        <div className="table-responsive">
          <table className="data-table moderation-table">
            <thead>
              <tr>
                <th>Sản phẩm</th>
                <th>Khách hàng</th>
                <th>Đánh giá & Nhận xét</th>
                <th>Trạng thái</th>
                <th>Thời gian</th>
                <th>Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {items.map(item => (
                <tr key={item.id} className={item.reviewStatus === 'HIDDEN' ? 'row-hidden' : ''}>
                  <td className="product-col">
                    <strong>{item.productTitle}</strong>
                  </td>
                  <td className="customer-col">
                    <span className="customer-name">{item.customerName}</span>
                    <small className="customer-email">{item.customerEmail}</small>
                  </td>
                  <td className="content-col">
                    <div className="rating-row">
                      <RatingStars value={item.rating} size="sm" />
                      <span className="rating-badge-number">{item.rating}/5</span>
                    </div>
                    <p className="moderation-content-text">{item.content}</p>
                    {item.reviewStatus === 'HIDDEN' && item.moderationReason && (
                      <div className="moderation-reason-box">
                        <small>
                          <strong>Lý do ẩn:</strong> {item.moderationReason}
                          {item.moderatorName && ` (bởi ${item.moderatorName})`}
                        </small>
                      </div>
                    )}
                  </td>
                  <td className="status-col">
                    <span className={`badge ${item.reviewStatus === 'PUBLISHED' ? 'badge-success' : 'badge-danger'}`}>
                      {item.reviewStatus === 'PUBLISHED' ? 'Hiển thị' : 'Đã ẩn'}
                    </span>
                  </td>
                  <td className="time-col">
                    <small>{formatDate(item.createdAt)}</small>
                  </td>
                  <td className="actions-col">
                    {item.reviewStatus === 'PUBLISHED' ? (
                      <button
                        type="button"
                        className="button danger btn-sm btn-hide-review"
                        onClick={() => {
                          setHidingTarget(item)
                          setHideReason('')
                          setHideReasonError(null)
                        }}
                      >
                        Ẩn
                      </button>
                    ) : (
                      <button
                        type="button"
                        className="button secondary btn-sm btn-restore-review"
                        onClick={() => setRestoringTarget(item)}
                      >
                        Khôi phục
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {totalPages > 1 && (
        <div className="moderation-pagination">
          <button
            type="button"
            className="pagination-btn"
            disabled={page <= 1 || loading}
            onClick={() => setPage(p => p - 1)}
          >
            ← Trước
          </button>
          <span>Trang {page} / {totalPages} ({totalCount} đánh giá)</span>
          <button
            type="button"
            className="pagination-btn"
            disabled={page >= totalPages || loading}
            onClick={() => setPage(p => p + 1)}
          >
            Sau →
          </button>
        </div>
      )}
    </div>
  )
}
