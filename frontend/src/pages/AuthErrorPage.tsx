import { Link, useLocation, useRevalidator, useRouteError } from 'react-router-dom'
import { ApiError } from '../lib/api/client'

export function AuthErrorPage() {
  const error = useRouteError()
  const revalidator = useRevalidator()
  const forbidden = error instanceof ApiError && error.status === 403
  const isCart = useLocation().pathname.startsWith('/cart')
  const correlationId = error instanceof ApiError ? error.correlationId : undefined

  return (
    <section className="content-page premium-error">
      <div className="empty-state">
        <span className="error-emblem" aria-hidden="true">{forbidden ? '403' : '!'}</span>
        <div role="alert">
          <h1>
            {forbidden
              ? 'Bạn không có quyền truy cập.'
              : isCart
              ? 'Chưa thể tải giỏ hàng.'
              : 'Chưa thể kiểm tra phiên đăng nhập.'}
          </h1>
          <p>
            {forbidden
              ? 'Hãy dùng tài khoản có quyền phù hợp.'
              : isCart
              ? 'Đã xảy ra sự cố khi đồng bộ hoặc đọc giỏ hàng của bạn. Hãy thử tải lại.'
              : 'Kiểm tra kết nối và thử lại sau.'}
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
