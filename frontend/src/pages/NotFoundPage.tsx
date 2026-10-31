import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <main className="content-page premium-error"><div className="empty-state">
      <span className="error-emblem" aria-hidden="true">404</span>
      <h1>Không tìm thấy trang</h1>
      <p className="page-intro">Trang bạn tìm có thể đã đổi địa chỉ hoặc không còn tồn tại. Hãy quay lại cửa hàng để tiếp tục khám phá.</p>
      <div style={{ marginTop: 28 }}>
        <Link to="/" className="button-primary">Về cửa hàng</Link>
      </div>
    </div></main>
  )
}
