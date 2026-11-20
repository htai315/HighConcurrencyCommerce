import { useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { PageHeading } from '../components/layout/PageHeading'
import { ConfirmDialog } from '../components/layout/ConfirmDialog'
import { formatPrice } from '../features/catalog/catalog.api'
import {
  cancelReturnRequest,
  customerReturnsLoader,
  returnReasonLabels,
  returnStatusLabels,
  type CustomerOrderReturnPage,
  type CustomerOrderReturnSummary,
} from '../features/returns/returns.api'

function formatDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('vi-VN') : '—'
}

const exchangeStages = ['REQUESTED', 'APPROVED', 'RECEIVED', 'EXCHANGE_READY', 'EXCHANGE_PACKED', 'EXCHANGE_SHIPPING', 'EXCHANGED']

function ExchangeTimeline({ status }: { status: string }) {
  const current = exchangeStages.indexOf(status)
  const terminalFailure = status === 'EXCHANGE_EXPIRED' || status === 'EXCHANGE_DELIVERY_FAILED' || status === 'REJECTED' || status === 'CANCELLED'
  return (
    <div className="exchange-timeline" aria-label="Tiến trình đổi sản phẩm">
      {exchangeStages.map((stage, index) => (
        <div className={`exchange-timeline__step ${index <= current ? 'is-complete' : ''}`} key={stage}>
          <span aria-hidden="true">{index < current || status === 'EXCHANGED' ? '✓' : index + 1}</span>
          <small>{returnStatusLabels[stage]}</small>
        </div>
      ))}
      {terminalFailure && <p className="exchange-timeline__failure">{returnStatusLabels[status] ?? status}</p>}
    </div>
  )
}

export function CustomerReturnsPage() {
  const initial = useLoaderData<typeof customerReturnsLoader>()
  return <CustomerReturnsView initial={initial} />
}

function CustomerReturnsView({ initial }: { initial: CustomerOrderReturnPage }) {
  const [returns, setReturns] = useState<CustomerOrderReturnSummary[]>(initial.items)
  const [cancellingReturn, setCancellingReturn] = useState<CustomerOrderReturnSummary | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  async function handleConfirmCancel() {
    if (!cancellingReturn) return
    setBusy(true)
    setMessage(null)
    try {
      const updated = await cancelReturnRequest(cancellingReturn.id)
      setReturns(prev =>
        prev.map(r => (r.id === updated.id ? { ...r, returnStatus: updated.returnStatus, updatedAt: updated.updatedAt } : r))
      )
      setMessage({ kind: 'success', text: `Đã hủy yêu cầu trả hàng cho đơn ${cancellingReturn.orderNumber}.` })
    } catch {
      setMessage({ kind: 'error', text: 'Không thể hủy yêu cầu trả hàng ở trạng thái hiện tại.' })
    } finally {
      setBusy(false)
      setCancellingReturn(null)
    }
  }

  return (
    <section className="content-page customer-returns-page">
      <PageHeading
        title="Yêu cầu đổi / trả hàng"
        description="Theo dõi và quản lý các yêu cầu đổi trả, hoàn tiền cho đơn COD."
      />

      {message && (
        <div className={`alert ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'} style={{ marginBottom: 20 }}>
          {message.text}
        </div>
      )}

      {cancellingReturn && (
        <ConfirmDialog
          title={`Hủy yêu cầu đổi/trả cho đơn ${cancellingReturn.orderNumber}?`}
          description="Sau khi hủy, bạn sẽ không thể mở lại yêu cầu này."
          confirmLabel="Xác nhận hủy"
          busy={busy}
          onCancel={() => setCancellingReturn(null)}
          onConfirm={() => void handleConfirmCancel()}
        />
      )}

      {returns.length === 0 ? (
        <div className="empty-state" style={{ textAlign: 'center', padding: '48px 16px' }}>
          <p style={{ fontSize: 16, color: 'var(--muted)' }}>Bạn chưa có yêu cầu đổi/trả hàng nào.</p>
          <Link to="/orders" className="button primary" style={{ marginTop: 12 }}>
            Xem danh sách đơn hàng
          </Link>
        </div>
      ) : (
        <div style={{ display: 'grid', gap: 16 }}>
          {returns.map(item => (
            <article
              key={item.id}
              className="customer-order-card"
              style={{ border: '1px solid var(--line, #e2e8f0)', borderRadius: 10, padding: 16, background: '#ffffff' }}
            >
              <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 12 }}>
                <div>
                  <strong style={{ fontSize: 16, display: 'block' }}>
                    Đơn hàng #{item.orderNumber}
                  </strong>
                  <span className="resolution-badge">{item.resolutionType === 'EXCHANGE' ? 'Đổi sang SKU khác' : 'Trả hàng & hoàn tiền'}</span>
                  <span style={{ fontSize: 13, color: 'var(--muted)' }}>
                    Yêu cầu lúc: {formatDate(item.requestedAt)}
                  </span>
                </div>
                <div style={{ textAlign: 'right' }}>
                  <span
                    className={`badge badge-${item.returnStatus.toLowerCase()}`}
                    style={{
                      display: 'inline-block',
                      padding: '4px 8px',
                      borderRadius: 6,
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
                  <strong style={{ display: 'block', marginTop: 4, color: 'var(--brand)', fontSize: 15 }}>
                    {item.resolutionType === 'EXCHANGE' ? 'Không thu/hoàn chênh lệch' : formatPrice(item.refundAmount)}
                  </strong>
                </div>
              </header>

              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12, fontSize: 14 }}>
                <div>
                  <span style={{ color: 'var(--muted)', fontSize: 12, display: 'block' }}>Lý do trả hàng:</span>
                  <strong>{returnReasonLabels[item.reasonCode] ?? item.reasonCode}</strong>
                </div>
                <div>
                  <span style={{ color: 'var(--muted)', fontSize: 12, display: 'block' }}>Số lượng sản phẩm:</span>
                  <strong>{item.totalItemCount} món</strong>
                </div>
                <div>
                  <span style={{ color: 'var(--muted)', fontSize: 12, display: 'block' }}>Cập nhật lần cuối:</span>
                  <span>{formatDate(item.updatedAt)}</span>
                </div>
                {item.evidenceReference && (
                  <div>
                    <span style={{ color: 'var(--muted)', fontSize: 12, display: 'block' }}>Mã chứng từ hoàn tiền:</span>
                    <strong>{item.evidenceReference}</strong>
                  </div>
                )}
              </div>

              {item.resolutionType === 'EXCHANGE' && <ExchangeTimeline status={item.returnStatus} />}

              {item.returnStatus === 'REQUESTED' && (
                <div style={{ marginTop: 14, paddingTop: 12, borderTop: '1px solid var(--line, #f1f5f9)', textAlign: 'right' }}>
                  <button
                    type="button"
                    className="button secondary"
                    onClick={() => setCancellingReturn(item)}
                    disabled={busy}
                    style={{ fontSize: 13, padding: '4px 10px' }}
                  >
                    Hủy yêu cầu trả hàng
                  </button>
                </div>
              )}
            </article>
          ))}
        </div>
      )}
    </section>
  )
}

