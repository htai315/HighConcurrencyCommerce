import { useEffect, useState } from 'react'
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap'
import { formatPrice } from '../catalog/catalog.api'
import {
  createReturnRequest,
  getReturnEligibility,
  returnReasonLabels,
  type CustomerOrderReturnDetail,
  type OrderReturnEligibility,
} from './returns.api'

interface CreateReturnModalProps {
  orderId: string
  orderNumber: string
  onClose: () => void
  onCreated: (ret: CustomerOrderReturnDetail) => void
}

export function CreateReturnModal({ orderId, orderNumber, onClose, onCreated }: CreateReturnModalProps) {
  const [loading, setLoading] = useState(true)
  const [eligibility, setEligibility] = useState<OrderReturnEligibility | null>(null)
  const [selectedItems, setSelectedItems] = useState<Record<string, { selected: boolean; quantity: number; targetSkuId: string }>>({})
  const [resolutionType, setResolutionType] = useState<'RETURN' | 'EXCHANGE'>('RETURN')
  const [reasonCode, setReasonCode] = useState<string>('DEFECTIVE')
  const [customerNote, setCustomerNote] = useState<string>('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const dialogRef = useModalFocusTrap<HTMLDivElement>(true, onClose, busy)

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        setLoading(true)
        const data = await getReturnEligibility(orderId)
        if (cancelled) return
        setEligibility(data)
        const initialMap: Record<string, { selected: boolean; quantity: number; targetSkuId: string }> = {}
        for (const item of data.items) {
          initialMap[item.orderItemId] = { selected: true, quantity: item.orderedQuantity, targetSkuId: item.exchangeTargets.find(target => target.availableQty > 0)?.skuId ?? '' }
        }
        setSelectedItems(initialMap)
      } catch (err: unknown) {
        if (cancelled) return
        setError(err instanceof Error ? err.message : 'Không thể kiểm tra điều kiện trả hàng.')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [orderId])

  const eligibleItems = eligibility?.items ?? []
  const activeSelections = Object.entries(selectedItems)
    .filter(([, v]) => v.selected && v.quantity > 0)
    .map(([orderItemId, v]) => ({ orderItemId, requestedQuantity: v.quantity, targetSkuId: resolutionType === 'EXCHANGE' ? v.targetSkuId : null }))

  const estimatedRefund = activeSelections.reduce((sum, sel) => {
    const item = eligibleItems.find(i => i.orderItemId === sel.orderItemId)
    if (!item) return sum
    if (sel.requestedQuantity === item.orderedQuantity) {
      return sum + item.maxRefundableAmount
    }
    return sum + Math.floor((item.maxRefundableAmount * sel.requestedQuantity) / item.orderedQuantity)
  }, 0)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (activeSelections.length === 0) {
      setError('Vui lòng chọn ít nhất 1 sản phẩm để trả hàng.')
      return
    }

    if (reasonCode === 'OTHER' && !customerNote.trim()) {
      setError('Lý do "Khác" bắt buộc phải điền ghi chú giải thích.')
      return
    }

    if (resolutionType === 'EXCHANGE' && activeSelections.some(item => !item.targetSkuId)) {
      setError('Vui lòng chọn phân loại thay thế cho tất cả sản phẩm muốn đổi.')
      return
    }

    setBusy(true)
    setError(null)

    try {
      const created = await createReturnRequest(orderId, {
        resolutionType,
        reasonCode,
        customerNote: customerNote.trim() || null,
        items: activeSelections,
      })
      onCreated(created)
      onClose()
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Không thể gửi yêu cầu trả hàng. Vui lòng thử lại.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="confirm-overlay" role="presentation">
      <div
        className="confirm-dialog return-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="return-modal-title"
        tabIndex={-1}
        ref={dialogRef}
        style={{ maxWidth: 560, width: '90%', maxHeight: '90vh', overflowY: 'auto' }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
          <h2 id="return-modal-title" style={{ margin: 0, fontSize: 18 }}>
            Yêu cầu đổi/trả hàng · Đơn {orderNumber}
          </h2>
          <button
            type="button"
            className="text-button"
            onClick={onClose}
            disabled={busy}
            aria-label="Đóng cửa sổ"
            style={{ fontSize: 20, padding: 4 }}
          >
            ✕
          </button>
        </div>

        {loading ? (
          <p role="status">Đang kiểm tra điều kiện đổi/trả...</p>
        ) : error && !eligibility ? (
          <div className="alert error" role="alert">
            {error}
          </div>
        ) : eligibility && !eligibility.isEligible ? (
          <div style={{ padding: '12px 0' }}>
            <div className="alert error" role="alert" style={{ marginBottom: 16 }}>
              {eligibility.ineligibleReason || 'Đơn hàng không đủ điều kiện đổi/trả hàng.'}
            </div>
            <div style={{ textAlign: 'right' }}>
              <button type="button" className="button secondary" onClick={onClose}>
                Đóng
              </button>
            </div>
          </div>
        ) : (
          <form onSubmit={handleSubmit} style={{ display: 'grid', gap: 16 }}>
            {error && (
              <div className="alert error" role="alert">
                {error}
              </div>
            )}

            <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
              <legend style={{ fontWeight: 700, marginBottom: 8 }}>Bạn muốn cửa hàng xử lý thế nào?</legend>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 10 }}>
                <label className={`return-resolution-option ${resolutionType === 'RETURN' ? 'is-selected' : ''}`}>
                  <input type="radio" name="resolutionType" value="RETURN" checked={resolutionType === 'RETURN'} onChange={() => setResolutionType('RETURN')} />
                  <span><strong>Hoàn tiền</strong><small>Trả hàng và nhận lại số tiền thực trả</small></span>
                </label>
                <label className={`return-resolution-option ${resolutionType === 'EXCHANGE' ? 'is-selected' : ''}`}>
                  <input type="radio" name="resolutionType" value="EXCHANGE" checked={resolutionType === 'EXCHANGE'} onChange={() => setResolutionType('EXCHANGE')} />
                  <span><strong>Đổi phân loại</strong><small>Đổi sang SKU khác của cùng sản phẩm, không bù giá</small></span>
                </label>
              </div>
            </fieldset>

            <div>
              <span className="panel-kicker" style={{ display: 'block', marginBottom: 6 }}>
                1. Chọn sản phẩm và số lượng trả:
              </span>
              <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: 10 }}>
                {eligibleItems.map(item => {
                  const current = selectedItems[item.orderItemId] ?? { selected: false, quantity: item.orderedQuantity, targetSkuId: '' }
                  return (
                    <li
                      key={item.orderItemId}
                      style={{
                        padding: '10px 12px',
                        border: '1px solid var(--line, #e2e8f0)',
                        borderRadius: 8,
                        background: current.selected ? 'var(--paper-subtle, #f8fafc)' : '#ffffff',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        gap: 12,
                      }}
                    >
                      <label style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', flex: 1 }}>
                        <input
                          type="checkbox"
                          checked={current.selected}
                          disabled={busy}
                          onChange={e =>
                            setSelectedItems(prev => ({
                              ...prev,
                              [item.orderItemId]: { ...current, selected: e.target.checked },
                            }))
                          }
                        />
                        <div>
                          <strong style={{ fontSize: 14 }}>{item.productTitle}</strong>
                          <span style={{ display: 'block', fontSize: 12, color: 'var(--muted)' }}>
                            {item.optionLabel} · Đã mua: {item.orderedQuantity} · Đơn giá: {formatPrice(item.unitPrice)}
                          </span>
                        </div>
                      </label>
                      {current.selected && (
                        <div style={{ display: 'grid', alignItems: 'center', gap: 6, minWidth: resolutionType === 'EXCHANGE' ? 190 : 90 }}>
                          <label htmlFor={`qty-${item.orderItemId}`} style={{ fontSize: 12 }}>
                            SL trả:
                          </label>
                          <input
                            id={`qty-${item.orderItemId}`}
                            type="number"
                            min={1}
                            max={item.orderedQuantity}
                            value={current.quantity}
                            disabled={busy}
                            onChange={e => {
                              const val = Math.max(1, Math.min(item.orderedQuantity, Number(e.target.value) || 1))
                              setSelectedItems(prev => ({
                                ...prev,
                                [item.orderItemId]: { ...current, quantity: val },
                              }))
                            }}
                            style={{ width: 60, padding: '4px 6px', textAlign: 'center' }}
                          />
                          {resolutionType === 'EXCHANGE' && (
                            <select
                              aria-label={`Phân loại thay thế ${item.productTitle}`}
                              value={current.targetSkuId}
                              onChange={event => setSelectedItems(previous => ({ ...previous, [item.orderItemId]: { ...current, targetSkuId: event.target.value } }))}
                              required
                            >
                              <option value="">Chọn SKU thay thế</option>
                              {item.exchangeTargets.map(target => (
                                <option key={target.skuId} value={target.skuId} disabled={target.availableQty < current.quantity}>
                                  {target.optionLabel} · còn {target.availableQty}
                                </option>
                              ))}
                            </select>
                          )}
                        </div>
                      )}
                    </li>
                  )
                })}
              </ul>
            </div>

            <div>
              <label htmlFor="return-reason" style={{ fontWeight: 600, display: 'block', marginBottom: 6 }}>
                2. Lý do đổi/trả hàng *
              </label>
              <select
                id="return-reason"
                value={reasonCode}
                onChange={e => setReasonCode(e.target.value)}
                disabled={busy}
                style={{ width: '100%', padding: '8px 10px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
              >
                {Object.entries(returnReasonLabels).map(([code, label]) => (
                  <option key={code} value={code}>
                    {label}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label htmlFor="customer-note" style={{ fontWeight: 600, display: 'block', marginBottom: 6 }}>
                3. Ghi chú chi tiết {reasonCode === 'OTHER' ? '*' : '(không bắt buộc)'}
              </label>
              <textarea
                id="customer-note"
                value={customerNote}
                onChange={e => setCustomerNote(e.target.value)}
                disabled={busy}
                rows={3}
                maxLength={1000}
                placeholder={
                  reasonCode === 'OTHER'
                    ? 'Vui lòng mô tả cụ thể lý do đổi/trả hàng...'
                    : 'Mô tả tình trạng hàng hóa hoặc ghi chú thêm...'
                }
                style={{ width: '100%', padding: '8px 10px', borderRadius: 6, border: '1px solid var(--line, #cbd5e1)' }}
              />
            </div>

            <div
              style={{
                padding: '12px 14px',
                background: 'var(--brand-soft, #f0fdf4)',
                border: '1px solid var(--brand-border, #bbf7d0)',
                borderRadius: 8,
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
              }}
            >
              <span>{resolutionType === 'EXCHANGE' ? 'Phương án xử lý:' : 'Số tiền dự kiến hoàn trả:'}</span>
              <strong style={{ fontSize: 16, color: 'var(--brand, #16a34a)' }}>{resolutionType === 'EXCHANGE' ? 'Đổi ngang giá · không thu/hoàn chênh lệch' : formatPrice(estimatedRefund)}</strong>
            </div>
            {resolutionType === 'RETURN' && eligibility && eligibility.shippingFee > 0 && (
              <p className="return-shipping-note">
                Phí vận chuyển {formatPrice(eligibility.shippingFee)} là dịch vụ đã thực hiện và không nằm trong số tiền hoàn. Tổng đơn ban đầu: {formatPrice(eligibility.orderTotalAmount)}.
              </p>
            )}

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10, marginTop: 8 }}>
              <button type="button" className="button secondary" onClick={onClose} disabled={busy}>
                Hủy bỏ
              </button>
              <button type="submit" className="button primary" disabled={busy || activeSelections.length === 0}>
                {busy ? 'Đang gửi yêu cầu...' : 'Gửi yêu cầu đổi/trả'}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  )
}

