import { useEffect, useRef, useState } from 'react'
import { Form, Link, NavLink, useLocation, useNavigation } from 'react-router-dom'
import type { AuthUser } from '../../features/auth/auth.api'
import './site-header.css'

function HeaderIcon({ kind }: { kind: 'search' | 'user' | 'cart' | 'chevron' }) {
  return <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    {kind === 'search' ? <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></> : kind === 'user' ? <><circle cx="12" cy="8" r="4" /><path d="M4 21v-2a8 8 0 0 1 16 0v2" /></> : kind === 'cart' ? <><path d="M3 4h2l2 12h12l2-9H6" /><circle cx="9" cy="20" r="1" /><circle cx="18" cy="20" r="1" /></> : <path d="m7 10 5 5 5-5" />}
  </svg>
}

export function SiteHeader({ user }: { user: AuthUser | null | undefined }) {
  const location = useLocation()
  const navigation = useNavigation()
  const [open, setOpen] = useState(false)
  const container = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!open) return
    function outside(event: PointerEvent) { if (!container.current?.contains(event.target as Node)) setOpen(false) }
    function escape(event: KeyboardEvent) { if (event.key === 'Escape') { setOpen(false); trigger.current?.focus() } }
    document.addEventListener('pointerdown', outside)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape) }
  }, [open])

  return <header className="shop-header">
    <div className="shop-header-main">
      <NavLink to="/" className="shop-logo" aria-label="High Commerce - trang chủ">
        <span className="shop-logo-mark"><svg viewBox="0 0 32 32" fill="none" stroke="currentColor" strokeWidth="2.3" strokeLinecap="round" aria-hidden="true"><path d="m8 5 7 14h3L25 5M12 5l5 14M21 5l-5 14M12 23c1 3 7 3 8 0" /></svg></span>
        <span>HighCommerce<small>Chuyên cầu lông</small></span>
      </NavLink>
      <Form key={location.search} method="get" action="/" role="search" aria-label="Tìm kiếm toàn cửa hàng" className="shop-search">
        <input aria-label="Tìm kiếm toàn cửa hàng" name="q" type="search" maxLength={100} defaultValue={new URLSearchParams(location.search).get('q') ?? ''} placeholder="Tìm vợt, giày, trang phục cầu lông…" />
        <button type="submit" aria-label="Tìm kiếm" disabled={navigation.state !== 'idle'}><HeaderIcon kind="search" /></button>
      </Form>
      <div className="shop-header-tools">
        {user ? <div className="shop-account" ref={container} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false) }}>
          <button type="button" ref={trigger} className="shop-tool" aria-expanded={open} aria-controls="shop-account-panel" aria-label="Tài khoản của bạn" onClick={() => setOpen(value => !value)}>
            <HeaderIcon kind="user" /><span className="shop-tool-copy"><small>Tài khoản</small><strong>{user.fullName}</strong></span><span className="shop-chevron"><HeaderIcon kind="chevron" /></span>
          </button>
          {open && <div id="shop-account-panel" className="shop-account-panel">
            <div className="shop-account-greeting"><strong>{user.fullName}</strong><span>{user.role === 'CUSTOMER' ? 'Tài khoản mua sắm' : user.role === 'STAFF' ? 'Nhân viên vận hành' : 'Quản trị viên'}</span></div>
            <nav aria-label="Tài khoản" onClick={() => setOpen(false)}>
              {user.role === 'CUSTOMER' && <NavLink to="/account">Hồ sơ & địa chỉ</NavLink>}
              <NavLink to="/notifications">Thông báo</NavLink>
              <NavLink to="/account/security">Bảo mật</NavLink>
            </nav>
            <Form method="post" action="/logout"><button type="submit" disabled={navigation.state !== 'idle'}>Đăng xuất</button></Form>
          </div>}
        </div> : <NavLink to="/login" className="shop-tool shop-login" aria-label="Đăng nhập"><HeaderIcon kind="user" /><span>Đăng nhập</span></NavLink>}
        <NavLink to="/cart" className="shop-tool shop-cart" aria-label="Mở giỏ hàng"><HeaderIcon kind="cart" /><span>Giỏ hàng</span></NavLink>
      </div>
    </div>
    <div className="shop-nav-row"><nav aria-label="Điều hướng chính" className="shop-nav">
      <NavLink to="/" end>Cửa hàng</NavLink>
      <Link to="/#catalog">Khám phá sản phẩm</Link>
      {(!user || user.role === 'CUSTOMER') && <NavLink to="/orders">Đơn hàng</NavLink>}
      {user?.role === 'STAFF' && <NavLink to="/staff" className="shop-workspace">Vận hành</NavLink>}
      {user?.role === 'ADMIN' && <NavLink to="/admin" className="shop-workspace">Quản trị</NavLink>}
    </nav><span className="shop-nav-note">Sẵn sàng cho buổi cầu tiếp theo</span></div>
  </header>
}
