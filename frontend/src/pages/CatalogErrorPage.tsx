import { Link, useRevalidator, useRouteError } from 'react-router-dom'
import { ApiError } from '../lib/api/client'

export function CatalogErrorPage() {
  const error = useRouteError()
  const revalidator = useRevalidator()
  const status = error instanceof ApiError ? error.status : 0
  const message = status === 404 ? 'Không tìm thấy sản phẩm.' : status === 400 ? 'Bộ lọc không hợp lệ.' : 'Không thể tải sản phẩm lúc này.'
  return <section className="content-page premium-error"><div className="empty-state">
    <span className="error-emblem" aria-hidden="true">{status === 404 ? '404' : '!'}</span>
    <div role="alert"><h1>{message}</h1><p>{status === 400 ? 'Kiểm tra giá, danh mục và số trang, hoặc xóa bộ lọc để bắt đầu lại.' : 'Bạn có thể trở về cửa hàng hoặc thử tải lại.'}</p></div>
    <div className="hero-actions"><Link to="/" className="button-secondary">Về cửa hàng / Xóa bộ lọc</Link>
      {status !== 404 && <button type="button" className="button-primary" disabled={revalidator.state !== 'idle'} onClick={() => void revalidator.revalidate()}>{revalidator.state !== 'idle' ? 'Đang tải…' : 'Thử lại'}</button>}
    </div>
    {error instanceof ApiError && error.correlationId && <p className="error-reference">Mã hỗ trợ: {error.correlationId}</p>}
  </div></section>
}
