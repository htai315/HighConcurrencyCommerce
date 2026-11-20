import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { formatPrice } from '../catalog/catalog.api'
import {
  type AdminVoucherSummary,
  type CreateVoucherPayload,
  type DiscountType,
  type UpdateVoucherPayload,
  type VoucherStatus,
  activateAdminVoucher,
  createAdminVoucher,
  endAdminVoucher,
  listAdminVouchers,
  pauseAdminVoucher,
  resumeAdminVoucher,
  updateAdminVoucher,
} from './voucher.api'

function toLocalDatetimeString(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString('vi-VN')
  } catch {
    return iso
  }
}

const statusBadgeClass: Record<VoucherStatus, string> = {
  DRAFT: 'badge-muted',
  ACTIVE: 'badge-success',
  PAUSED: 'badge-warning',
  ENDED: 'badge-danger',
}

const statusLabel: Record<VoucherStatus, string> = {
  DRAFT: 'Bản nháp',
  ACTIVE: 'Đang mở',
  PAUSED: 'Tạm dừng',
  ENDED: 'Đã kết thúc',
}

export function AdminVouchersPanel() {
  const [vouchers, setVouchers] = useState<AdminVoucherSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [statusFilter, setStatusFilter] = useState<string>('ALL')
  const [searchQuery, setSearchQuery] = useState('')
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // Confirmation modal
  const [confirmDialog, setConfirmDialog] = useState<{
    title: string
    description: string
    action: () => Promise<void>
  } | null>(null)

  // Form state
  const [showForm, setShowForm] = useState(false)
  const [editingVoucher, setEditingVoucher] = useState<AdminVoucherSummary | null>(null)

  const [formCode, setFormCode] = useState('')
  const [formName, setFormName] = useState('')
  const [formDiscountType, setFormDiscountType] = useState<DiscountType>('FIXED_AMOUNT')
  const [formDiscountValue, setFormDiscountValue] = useState('50000')
  const [formMinOrder, setFormMinOrder] = useState('100000')
  const [formMaxDiscount, setFormMaxDiscount] = useState('')
  const [formUsageLimitTotal, setFormUsageLimitTotal] = useState('')
  const [formPerCustomer, setFormPerCustomer] = useState('1')
  const [formTotalBudget, setFormTotalBudget] = useState('')
  const [formStartsAt, setFormStartsAt] = useState(() => {
    const d = new Date()
    return toLocalDatetimeString(d)
  })
  const [formEndsAt, setFormEndsAt] = useState(() => {
    const d = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000)
    return toLocalDatetimeString(d)
  })

  const loadVouchers = useCallback(async () => {
    try {
      const page = await listAdminVouchers({
        status: statusFilter,
        q: searchQuery.trim() || undefined,
        pageSize: 100,
      })
      setVouchers(page.items)
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải danh sách mã giảm giá.' })
    } finally {
      setLoading(false)
    }
  }, [statusFilter, searchQuery])

  useEffect(() => {
    let ignore = false
    listAdminVouchers({
      status: statusFilter,
      q: searchQuery.trim() || undefined,
      pageSize: 100,
    })
      .then(page => {
        if (!ignore) setVouchers(page.items)
      })
      .catch((err: unknown) => {
        if (!ignore) {
          setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải danh sách mã giảm giá.' })
        }
      })
      .finally(() => {
        if (!ignore) setLoading(false)
      })

    return () => {
      ignore = true
    }
  }, [statusFilter, searchQuery])

  function resetForm() {
    setEditingVoucher(null)
    setFormCode('')
    setFormName('')
    setFormDiscountType('FIXED_AMOUNT')
    setFormDiscountValue('50000')
    setFormMinOrder('100000')
    setFormMaxDiscount('')
    setFormUsageLimitTotal('')
    setFormPerCustomer('1')
    setFormTotalBudget('')
    const now = new Date()
    setFormStartsAt(toLocalDatetimeString(now))
    setFormEndsAt(toLocalDatetimeString(new Date(now.getTime() + 7 * 24 * 60 * 60 * 1000)))
    setShowForm(false)
  }

  function startEdit(v: AdminVoucherSummary) {
    if (v.voucherStatus !== 'DRAFT') return
    setEditingVoucher(v)
    setFormCode(v.code)
    setFormName(v.name)
    setFormDiscountType(v.discountType)
    setFormDiscountValue(String(v.discountValue))
    setFormMinOrder(String(v.minimumOrderAmount))
    setFormMaxDiscount(v.maxDiscountAmount ? String(v.maxDiscountAmount) : '')
    setFormUsageLimitTotal(v.usageLimitTotal ? String(v.usageLimitTotal) : '')
    setFormPerCustomer(String(v.usageLimitPerCustomer))
    setFormTotalBudget(v.totalBudgetAmount ? String(v.totalBudgetAmount) : '')
    setFormStartsAt(toLocalDatetimeString(new Date(v.startsAt)))
    setFormEndsAt(toLocalDatetimeString(new Date(v.endsAt)))
    setShowForm(true)
  }

  async function handleSaveVoucher(e: React.FormEvent) {
    e.preventDefault()
    setSubmitting(true)
    setMessage(null)

    try {
      const startsDate = new Date(formStartsAt)
      const endsDate = new Date(formEndsAt)
      if (isNaN(startsDate.getTime()) || isNaN(endsDate.getTime())) {
        throw new Error('Thời gian bắt đầu và kết thúc phải hợp lệ.')
      }
      if (endsDate <= startsDate) {
        throw new Error('Thời gian kết thúc phải sau thời gian bắt đầu.')
      }

      const discVal = Number(formDiscountValue)
      if (!Number.isFinite(discVal) || discVal <= 0) {
        throw new Error('Mức giảm giá phải là số dương.')
      }
      if (formDiscountType === 'PERCENTAGE' && discVal > 100) {
        throw new Error('Tỷ lệ giảm giá không được vượt quá 100%.')
      }

      const minOrder = Number(formMinOrder) || 0

      if (editingVoucher) {
        const payload: UpdateVoucherPayload = {
          code: editingVoucher.code,
          name: formName.trim(),
          discountType: formDiscountType,
          discountValue: discVal,
          minimumOrderAmount: minOrder,
          maxDiscountAmount: formDiscountType === 'PERCENTAGE' && formMaxDiscount.trim() ? Number(formMaxDiscount) : null,
          usageLimitTotal: formUsageLimitTotal.trim() ? Number(formUsageLimitTotal) : null,
          usageLimitPerCustomer: Number(formPerCustomer) || 1,
          totalBudgetAmount: formTotalBudget.trim() ? Number(formTotalBudget) : null,
          startsAt: startsDate.toISOString(),
          endsAt: endsDate.toISOString(),
        }
        await updateAdminVoucher(editingVoucher.id, payload)
        setMessage({ type: 'success', text: `Đã cập nhật mã giảm giá "${editingVoucher.code}".` })
      } else {
        const payload: CreateVoucherPayload = {
          code: formCode.trim().toUpperCase(),
          name: formName.trim(),
          discountType: formDiscountType,
          discountValue: discVal,
          minimumOrderAmount: minOrder,
          maxDiscountAmount: formDiscountType === 'PERCENTAGE' && formMaxDiscount.trim() ? Number(formMaxDiscount) : null,
          usageLimitTotal: formUsageLimitTotal.trim() ? Number(formUsageLimitTotal) : null,
          usageLimitPerCustomer: Number(formPerCustomer) || 1,
          totalBudgetAmount: formTotalBudget.trim() ? Number(formTotalBudget) : null,
          startsAt: startsDate.toISOString(),
          endsAt: endsDate.toISOString(),
        }
        await createAdminVoucher(payload)
        setMessage({ type: 'success', text: `Đã tạo mã giảm giá "${payload.code}" (Bản nháp).` })
      }

      resetForm()
      await loadVouchers()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể lưu mã giảm giá.' })
    } finally {
      setSubmitting(false)
    }
  }

  function handleAction(action: 'activate' | 'pause' | 'resume' | 'end', v: AdminVoucherSummary) {
    const actionText = {
      activate: 'Kích hoạt',
      pause: 'Tạm dừng',
      resume: 'Mở lại',
      end: 'Kết thúc vĩnh viễn',
    }[action]

    setConfirmDialog({
      title: `${actionText} mã giảm giá`,
      description: `Bạn có chắc muốn ${actionText.toLowerCase()} mã ưu đãi "${v.code}"?`,
      action: async () => {
        try {
          if (action === 'activate') await activateAdminVoucher(v.id)
          else if (action === 'pause') await pauseAdminVoucher(v.id)
          else if (action === 'resume') await resumeAdminVoucher(v.id)
          else if (action === 'end') await endAdminVoucher(v.id)

          setMessage({ type: 'success', text: `Đã thực hiện "${actionText}" thành công cho mã "${v.code}".` })
          await loadVouchers()
        } catch (err: unknown) {
          setMessage({ type: 'error', text: err instanceof Error ? err.message : `Không thể ${actionText.toLowerCase()} mã giảm giá.` })
        }
      },
    })
  }

  return (
    <div className="admin-panel admin-vouchers-panel" style={{ padding: '16px 0' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <div>
          <h2 style={{ margin: 0, fontSize: 20 }}>Quản lý mã giảm giá</h2>
          <p style={{ margin: '4px 0 0', color: 'var(--muted)', fontSize: 14 }}>
            Tạo, phân bổ và theo dõi ngân sách khuyến mãi cho đơn hàng.
          </p>
        </div>
        <button
          type="button"
          className="button-primary"
          onClick={() => { resetForm(); setShowForm(true) }}
          disabled={showForm}
        >
          + Tạo mã mới
        </button>
      </div>

      {message && (
        <div className={`alert ${message.type}`} role={message.type === 'error' ? 'alert' : 'status'} style={{ marginBottom: 16 }}>
          {message.text}
        </div>
      )}

      {showForm && (
        <form onSubmit={handleSaveVoucher} style={{ background: 'var(--surface-muted, #f8fafc)', border: '1px solid var(--border-color, #e2e8f0)', borderRadius: 8, padding: 16, marginBottom: 20 }}>
          <h3 style={{ margin: '0 0 14px', fontSize: 16 }}>
            {editingVoucher ? `Chỉnh sửa mã: ${editingVoucher.code}` : 'Tạo mã giảm giá mới'}
          </h3>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 12 }}>
            {!editingVoucher && (
              <div>
                <label htmlFor="vch-code" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                  Mã voucher (In hoa, 3-32 ký tự) *
                </label>
                <input
                  id="vch-code"
                  type="text"
                  required
                  value={formCode}
                  placeholder="VD: WELCOME10"
                  onChange={e => setFormCode(e.target.value.toUpperCase())}
                  pattern="^[A-Za-z0-9_-]{3,32}$"
                  style={{ width: '100%', padding: '6px 10px', textTransform: 'uppercase' }}
                />
              </div>
            )}

            <div>
              <label htmlFor="vch-name" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Tên chương trình *
              </label>
              <input
                id="vch-name"
                type="text"
                required
                value={formName}
                placeholder="VD: Giảm giá khách hàng mới"
                onChange={e => setFormName(e.target.value)}
                maxLength={100}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-discount-type" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Loại giảm giá *
              </label>
              <select
                id="vch-discount-type"
                value={formDiscountType}
                onChange={e => setFormDiscountType(e.target.value as DiscountType)}
                style={{ width: '100%', padding: '6px 10px' }}
              >
                <option value="FIXED_AMOUNT">Số tiền cố định (VNĐ)</option>
                <option value="PERCENTAGE">Phần trăm (%)</option>
              </select>
            </div>

            <div>
              <label htmlFor="vch-discount-value" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Giá trị giảm * ({formDiscountType === 'PERCENTAGE' ? '%' : 'VNĐ'})
              </label>
              <input
                id="vch-discount-value"
                type="number"
                required
                min="1"
                max={formDiscountType === 'PERCENTAGE' ? 100 : 999999999}
                value={formDiscountValue}
                onChange={e => setFormDiscountValue(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-min-order" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Giá trị đơn tối thiểu (VNĐ)
              </label>
              <input
                id="vch-min-order"
                type="number"
                min="0"
                value={formMinOrder}
                onChange={e => setFormMinOrder(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            {formDiscountType === 'PERCENTAGE' && (
              <div>
                <label htmlFor="vch-max-discount" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                  Mức giảm tối đa (VNĐ)
                </label>
                <input
                  id="vch-max-discount"
                  type="number"
                  min="1"
                  value={formMaxDiscount}
                  placeholder="Không giới hạn"
                  onChange={e => setFormMaxDiscount(e.target.value)}
                  style={{ width: '100%', padding: '6px 10px' }}
                />
              </div>
            )}

            <div>
              <label htmlFor="vch-usage-limit-total" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Tổng lượt sử dụng
              </label>
              <input
                id="vch-usage-limit-total"
                type="number"
                min="1"
                value={formUsageLimitTotal}
                placeholder="Không giới hạn"
                onChange={e => setFormUsageLimitTotal(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-per-customer" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Lượt dùng mỗi khách *
              </label>
              <input
                id="vch-per-customer"
                type="number"
                required
                min="1"
                value={formPerCustomer}
                onChange={e => setFormPerCustomer(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-total-budget" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Tổng ngân sách (VNĐ)
              </label>
              <input
                id="vch-total-budget"
                type="number"
                min="1"
                value={formTotalBudget}
                placeholder="Không giới hạn"
                onChange={e => setFormTotalBudget(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-starts-at" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Bắt đầu lúc *
              </label>
              <input
                id="vch-starts-at"
                type="datetime-local"
                required
                value={formStartsAt}
                onChange={e => setFormStartsAt(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>

            <div>
              <label htmlFor="vch-ends-at" style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                Kết thúc lúc *
              </label>
              <input
                id="vch-ends-at"
                type="datetime-local"
                required
                value={formEndsAt}
                onChange={e => setFormEndsAt(e.target.value)}
                style={{ width: '100%', padding: '6px 10px' }}
              />
            </div>
          </div>

          <div style={{ display: 'flex', gap: 10, marginTop: 16 }}>
            <button type="submit" className="button-primary" disabled={submitting}>
              {submitting ? 'Đang lưu…' : editingVoucher ? 'Cập nhật' : 'Tạo bản nháp'}
            </button>
            <button type="button" className="button-secondary" onClick={resetForm} disabled={submitting}>
              Hủy
            </button>
          </div>
        </form>
      )}

      {/* Filter and Search Bar */}
      <div style={{ display: 'flex', gap: 12, marginBottom: 16, alignItems: 'center', flexWrap: 'wrap' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <label htmlFor="voucher-status-filter" style={{ fontSize: 13, fontWeight: 600 }}>Trạng thái:</label>
          <select
            id="voucher-status-filter"
            value={statusFilter}
            onChange={e => setStatusFilter(e.target.value)}
            style={{ padding: '6px 10px', fontSize: 13 }}
          >
            <option value="ALL">Tất cả</option>
            <option value="DRAFT">Bản nháp</option>
            <option value="ACTIVE">Đang mở</option>
            <option value="PAUSED">Tạm dừng</option>
            <option value="ENDED">Đã kết thúc</option>
          </select>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 200 }}>
          <input
            type="text"
            aria-label="Tìm kiếm voucher theo mã hoặc tên"
            value={searchQuery}
            placeholder="Tìm theo mã hoặc tên voucher…"
            onChange={e => setSearchQuery(e.target.value)}
            style={{ width: '100%', padding: '6px 10px', fontSize: 13 }}
          />
        </div>
      </div>

      {loading ? (
        <p style={{ textAlign: 'center', color: 'var(--muted)', padding: 24 }}>Đang tải danh sách voucher…</p>
      ) : vouchers.length === 0 ? (
        <p style={{ textAlign: 'center', color: 'var(--muted)', padding: 24 }}>Chưa có mã giảm giá nào phù hợp.</p>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="data-table" style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ borderBottom: '2px solid var(--border-color, #e2e8f0)', textAlign: 'left' }}>
                <th style={{ padding: '8px 10px' }}>Mã / Tên</th>
                <th style={{ padding: '8px 10px' }}>Mức giảm</th>
                <th style={{ padding: '8px 10px' }}>Đơn tối thiểu</th>
                <th style={{ padding: '8px 10px' }}>Lượt dùng (Held / Đã dùng / Giới hạn)</th>
                <th style={{ padding: '8px 10px' }}>Ngân sách (Held / Đã dùng / Tổng)</th>
                <th style={{ padding: '8px 10px' }}>Thời gian</th>
                <th style={{ padding: '8px 10px' }}>Trạng thái</th>
                <th style={{ padding: '8px 10px', textAlign: 'right' }}>Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {vouchers.map(v => (
                <tr key={v.id} style={{ borderBottom: '1px solid var(--border-color, #e2e8f0)' }}>
                  <td style={{ padding: '10px 10px' }}>
                    <strong style={{ fontSize: 14 }}>{v.code}</strong>
                    <div style={{ color: 'var(--muted)', fontSize: 12 }}>{v.name}</div>
                  </td>
                  <td style={{ padding: '10px 10px' }}>
                    {v.discountType === 'FIXED_AMOUNT' ? (
                      formatPrice(v.discountValue)
                    ) : (
                      <>
                        {v.discountValue}%
                        {v.maxDiscountAmount && (
                          <div style={{ fontSize: 11, color: 'var(--muted)' }}>
                            Tối đa: {formatPrice(v.maxDiscountAmount)}
                          </div>
                        )}
                      </>
                    )}
                  </td>
                  <td style={{ padding: '10px 10px' }}>{formatPrice(v.minimumOrderAmount)}</td>
                  <td style={{ padding: '10px 10px' }}>
                    <div>
                      {v.usageHeld} / {v.usageConsumed} / {v.usageLimitTotal ?? '∞'}
                    </div>
                    <div style={{ fontSize: 11, color: 'var(--muted)' }}>
                      Tối đa {v.usageLimitPerCustomer} lần/khách
                    </div>
                  </td>
                  <td style={{ padding: '10px 10px' }}>
                    <div>
                      {formatPrice(v.budgetHeld)} / {formatPrice(v.budgetConsumed)}
                    </div>
                    <div style={{ fontSize: 11, color: 'var(--muted)' }}>
                      Tổng: {v.totalBudgetAmount ? formatPrice(v.totalBudgetAmount) : '∞'}
                    </div>
                  </td>
                  <td style={{ padding: '10px 10px', fontSize: 12 }}>
                    <div>{formatDate(v.startsAt)}</div>
                    <div style={{ color: 'var(--muted)' }}>đến {formatDate(v.endsAt)}</div>
                  </td>
                  <td style={{ padding: '10px 10px' }}>
                    <span className={`badge ${statusBadgeClass[v.voucherStatus]}`}>
                      {statusLabel[v.voucherStatus]}
                    </span>
                  </td>
                  <td style={{ padding: '10px 10px', textAlign: 'right' }}>
                    <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                      {v.voucherStatus === 'DRAFT' && (
                        <>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12 }}
                            onClick={() => startEdit(v)}
                          >
                            Sửa
                          </button>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12, color: 'var(--success-ink, #16a34a)' }}
                            onClick={() => handleAction('activate', v)}
                          >
                            Kích hoạt
                          </button>
                        </>
                      )}

                      {v.voucherStatus === 'ACTIVE' && (
                        <>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12, color: 'var(--warning-ink, #d97706)' }}
                            onClick={() => handleAction('pause', v)}
                          >
                            Tạm dừng
                          </button>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12, color: 'var(--danger-ink, #dc2626)' }}
                            onClick={() => handleAction('end', v)}
                          >
                            Kết thúc
                          </button>
                        </>
                      )}

                      {v.voucherStatus === 'PAUSED' && (
                        <>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12, color: 'var(--success-ink, #16a34a)' }}
                            onClick={() => handleAction('resume', v)}
                          >
                            Mở lại
                          </button>
                          <button
                            type="button"
                            className="text-button"
                            style={{ fontSize: 12, color: 'var(--danger-ink, #dc2626)' }}
                            onClick={() => handleAction('end', v)}
                          >
                            Kết thúc
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {confirmDialog && (
        <ConfirmDialog
          title={confirmDialog.title}
          description={confirmDialog.description}
          confirmLabel="Xác nhận"
          onConfirm={async () => {
            await confirmDialog.action()
            setConfirmDialog(null)
          }}
          onCancel={() => setConfirmDialog(null)}
        />
      )}
    </div>
  )
}
