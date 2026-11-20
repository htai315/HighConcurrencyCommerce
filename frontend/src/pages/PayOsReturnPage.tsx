import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { PageHeading } from '../components/layout/PageHeading'
import { StatusBadge } from '../components/layout/StatusBadge'
import { formatPrice } from '../features/catalog/catalog.api'
import {
  cancelPayOsPayment,
  createPayOsAttempt,
  getPayOsPaymentStatus,
  payOsReturnLoader,
  redirectToPayOs,
  rememberPayOsReturnOrder,
  type PayOsPaymentStatus,
} from '../features/payments/payos.api'
import { ApiError } from '../lib/api/client'

const terminal = new Set(['SUCCESS', 'VOID', 'LATE_PAID'])

export function PayOsReturnPage() {
  const { orderId, providerCancelled, initialStatus, initialLoadFailed } = useLoaderData<typeof payOsReturnLoader>()
  const [status, setStatus] = useState<PayOsPaymentStatus | null>(initialStatus)
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(initialLoadFailed
    ? 'Chưa thể xác minh thanh toán. Vui lòng thử lại.'
    : null)
  const refreshController = useRef<AbortController | null>(null)

  const refresh = useCallback(async (force = false) => {
    if (!orderId || (refreshController.current && !force)) return
    if (force) refreshController.current?.abort()
    const controller = new AbortController()
    refreshController.current = controller
    setLoading(true)
    try {
      const next = await getPayOsPaymentStatus(orderId, controller.signal)
      if (refreshController.current !== controller) return
      setStatus(next)
      setMessage(null)
    } catch (error) {
      if (controller.signal.aborted || refreshController.current !== controller) return
      setMessage(error instanceof ApiError ? 'Chưa thể xác minh thanh toán. Vui lòng thử lại.' : 'Kết nối không ổn định.')
    } finally {
      if (refreshController.current === controller) {
        refreshController.current = null
        setLoading(false)
      }
    }
  }, [orderId])

  useEffect(() => () => refreshController.current?.abort(), [])

  useEffect(() => {
    if (!orderId || (status && (terminal.has(status.paymentStatus)
      || ['CANCELLED', 'EXPIRED', 'CONFIRMED'].includes(status.orderStatus)))) return
    const timer = window.setInterval(() => void refresh(), 2_500)
    return () => window.clearInterval(timer)
  }, [orderId, refresh, status])

  async function retryPayment() {
    if (!orderId || busy) return
    setBusy(true); setMessage(null)
    try {
      const next = await createPayOsAttempt(orderId)
      if (!next.checkoutUrl) throw new Error('missing checkout url')
      rememberPayOsReturnOrder(orderId)
      redirectToPayOs(next.checkoutUrl)
    } catch {
      setMessage('Chưa thể mở lại cổng thanh toán. Trạng thái đơn vẫn được giữ an toàn.')
    } finally { setBusy(false) }
  }

  async function cancel() {
    if (!orderId || busy) return
    setBusy(true); setMessage(null)
    try {
      await cancelPayOsPayment(orderId, 'Khách hủy từ trang trạng thái PayOS')
      await refresh(true)
    } catch (error) {
      setMessage(error instanceof ApiError && error.code === 'PAYMENT_ALREADY_PAID'
        ? 'PayOS đã ghi nhận thanh toán. Hệ thống đang cập nhật đơn hàng.'
        : 'Chưa thể xác nhận hủy với PayOS; hàng vẫn được giữ và đơn chưa bị đóng.')
    } finally { setBusy(false) }
  }

  const paid = status?.paymentStatus === 'SUCCESS'
  const reconciliation = status?.paymentStatus === 'LATE_PAID'
  const closed = status?.paymentStatus === 'VOID' || status?.orderStatus === 'CANCELLED' || status?.orderStatus === 'EXPIRED'

  return <section className="content-page payos-return-page">
    <PageHeading title="Trạng thái thanh toán PayOS" description="Kết quả luôn được xác minh trực tiếp từ hệ thống cửa hàng, không dựa vào thông báo trên trình duyệt." extra={<Link to="/orders" className="back-link">Xem đơn hàng</Link>} />
    {!orderId ? <div className="empty-state"><h2>Không tìm thấy phiên thanh toán</h2><p>Hãy mở đơn hàng của bạn để tiếp tục hoặc kiểm tra trạng thái.</p><Link to="/orders" className="button-primary">Đến đơn hàng</Link></div>
      : <article className={`payos-status-card ${paid ? 'is-success' : closed ? 'is-closed' : ''}`} aria-live="polite">
        <div className="payos-status-icon" aria-hidden="true">{paid ? '✓' : reconciliation ? '!' : closed ? '×' : '↻'}</div>
        <div>
          <p className="eyebrow">{status?.orderNumber ?? 'Đang tải đơn hàng'}</p>
          <h2>{paid ? 'Thanh toán thành công' : reconciliation ? 'Khoản tiền đang được đối soát' : closed ? 'Thanh toán đã đóng' : providerCancelled ? 'Đang xác nhận yêu cầu hủy' : 'Đang xác minh thanh toán'}</h2>
          <p>{paid ? 'Đơn hàng đã được xác nhận và chuyển sang khâu chuẩn bị.' : reconciliation ? 'Tiền đến sau khi đơn đã đóng. Shop sẽ kiểm tra và liên hệ với bạn.' : closed ? 'Giữ hàng đã được giải phóng an toàn.' : 'Bạn có thể chờ tại đây; trạng thái sẽ tự động cập nhật.'}</p>
        </div>
        {status && <div className="payos-status-grid">
          <div><span>Đơn hàng</span><StatusBadge value={status.orderStatus} /></div>
          <div><span>Thanh toán</span><StatusBadge value={status.paymentStatus} /></div>
          <div><span>Số tiền</span><strong>{formatPrice(status.expectedAmount)}</strong></div>
          <div><span>Thời hạn</span><strong>{new Date(status.deadlineAt).toLocaleString('vi-VN')}</strong></div>
        </div>}
        {loading && <p role="status">Đang kiểm tra với hệ thống…</p>}
        {message && <p role="alert" className="alert error">{message}</p>}
        <div className="payos-status-actions">
          {!paid && !reconciliation && !closed && <button type="button" className="button-primary" disabled={busy} onClick={() => void retryPayment()}>{busy ? 'Đang xử lý…' : 'Tiếp tục thanh toán'}</button>}
          {!paid && !reconciliation && !closed && <button type="button" className="button-secondary" disabled={busy} onClick={() => void cancel()}>Hủy thanh toán</button>}
          <button type="button" className="button-secondary" disabled={loading || busy} onClick={() => void refresh()}>Kiểm tra lại</button>
          <Link to="/orders" className="text-button">Về đơn hàng</Link>
        </div>
      </article>}
  </section>
}
