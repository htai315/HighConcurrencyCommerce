import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import hero from '../../assets/badminton-hero-v2.png'
import './auth.css'
import './auth-premium.css'

export function AuthLayout({ children }: { children: ReactNode }) {
  return <main className="court-auth auth-premium">
    <aside className="court-story">
      <Link to="/" className="court-brand" aria-label="HighCommerce - cửa hàng cầu lông">
        <span className="court-brand-symbol" aria-hidden="true"><svg viewBox="0 0 32 32" fill="none"><path d="m7 6 7 13 4-1 7-12M11 5l5 14M21 5l-5 14M12 22a4 4 0 0 0 8 0" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"/></svg></span>
        <span>HighCommerce<small>Chuyên cầu lông</small></span>
      </Link>
      <img className="auth-sport-photo" src={hero} alt="Vận động viên bật nhảy đập cầu trên sân cầu lông" />
      <div className="court-story-copy"><span className="auth-sport-tag">Cùng bạn trong từng pha cầu</span><h2>Nâng tầm<br/>trận đấu của bạn.</h2><p>Vợt vừa tay. Giày vững bước. Trang phục thoải mái.<br/>Tìm lựa chọn phù hợp với lối chơi của bạn.</p></div>
      <div className="court-story-footer"><span>Vợt & cầu</span><span>Giày & trang phục</span><span>Phụ kiện</span></div>
    </aside>
    <section className="court-form-panel">
      <Link to="/" className="court-back"><span aria-hidden="true">←</span> Trở về cửa hàng</Link>
      <div className="court-form-content">{children}</div>
      <footer className="court-form-footer">HighCommerce <span>Đồng hành cùng đam mê cầu lông.</span></footer>
    </section>
  </main>
}

