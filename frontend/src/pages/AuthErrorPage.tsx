import { Link, useLocation, useRevalidator, useRouteError } from 'react-router-dom'
import { ApiError } from '../lib/api/client'

export function AuthErrorPage() {
  const error = useRouteError()
  const revalidator = useRevalidator()
  const forbidden = error instanceof ApiError && error.status === 403
  const pathname = useLocation().pathname
  const context = pathname.startsWith('/cart')
    ? ['Chưa thể tải giỏ hàng.', 'Đã xảy ra sự cố khi đồng bộ hoặc đọc giỏ hàng của bạn. Hãy thử tải lại.']
    : pathname.startsWith('/wishlist')
      ? ['Chưa thể tải danh sách yêu thích.', 'Danh sách yêu thích chưa thể đồng bộ. Hãy thử tải lại.']
      : pathname.startsWith('/orders')
        ? ['Chưa thể tải đơn hàng.', 'Lịch sử đơn hàng chưa thể đồng bộ. Hãy thử tải lại.']
        : pathname.startsWith('/payment/payos')
          ? ['Chưa thể xác minh thanh toán.', 'Trạng thái PayOS chưa thể đồng bộ. Đơn hàng của bạn vẫn được giữ an toàn.']
          : pathname.startsWith('/admin') || pathname.startsWith('/staff')
            ? ['Chưa thể tải không gian vận hành.', 'Dữ liệu quản trị chưa thể đồng bộ. Hãy thử tải lại.']
            : pathname.startsWith('/account')
              ? ['Chưa thể tải tài khoản.', 'Thông tin tài khoản chưa thể đồng bộ. Hãy thử tải lại.']
              : pathname.startsWith('/notifications')
                ? ['Chưa thể tải thông báo.', 'Thông báo chưa thể đồng bộ. Hãy thử tải lại.']
                : ['Chưa thể kiểm tra phiên đăng nhập.', 'Kiểm tra kết nối và thử lại sau.']
  const correlationId = error instanceof ApiError ? error.correlationId : undefined

  return (
    <section className="content-page premium-error">
      <div className="empty-state">
        <span className="error-emblem" aria-hidden="true">{forbidden ? '403' : '!'}</span>
        <div role="alert">
          <h1>
            {forbidden
              ? 'Bạn không có quyền truy cập.'
              : context[0]}
          </h1>
          <p>
            {forbidden
              ? 'Hãy dùng tài khoản có quyền phù hợp.'
              : context[1]}
          </p>
        </div>

        <div className="hero-actions">
          <Link to="/" className="button-secondary">Về cửa hàng</Link>
          {!forbidden && (
            <button
              type="button"
              className="button-primary"
              onClick={() => void revalidator.revalidate()}
              disabled={revalidator.state !== 'idle'}
            >
              {revalidator.state !== 'idle' ? 'Đang tải…' : 'Thử lại'}
            </button>
          )}
        </div>

        {correlationId && <p className="error-reference">Mã hỗ trợ: {correlationId}</p>}
      </div>
    </section>
  )
}
