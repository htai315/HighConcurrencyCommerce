import { useEffect, useRef, useState } from 'react'
import { Form, Link, NavLink, useNavigation } from 'react-router-dom'
import type { AuthUser } from '../../features/auth/auth.api'
import './site-header.css'
import { readGuestCart } from '../../features/cart/guest-cart'
import { getCustomerCart } from '../../features/cart/cart.api'
import { useWishlist } from '../../features/wishlist/useWishlist'
import { CatalogSearchBox } from '../../features/catalog/CatalogSearchBox'

function HeaderIcon({ kind }: { kind: 'user' | 'cart' | 'chevron' | 'orders' | 'heart' }) {
  return <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    {kind === 'user' ? <><circle cx="12" cy="8" r="4" /><path d="M4 21v-2a8 8 0 0 1 16 0v2" /></> :
     kind === 'cart' ? <><path d="M3 4h2l2 12h12l2-9H6" /><circle cx="9" cy="20" r="1" /><circle cx="18" cy="20" r="1" /></> :
     kind === 'orders' ? <><path d="m16.5 9.4-9-5.19M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><polyline points="3.27 6.96 12 12.01 20.73 6.96"/><line x1="12" y1="22.08" x2="12" y2="12"/></> :
     kind === 'heart' ? <path d="M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" /> :
     <path d="m7 10 5 5 5-5" />}
  </svg>
}

export function SiteHeader({ user }: { user: AuthUser | null | undefined }) {
  const navigation = useNavigation()
  const [open, setOpen] = useState(false)
  const [cartCount, setCartCount] = useState<number | null>(null)
  const { totalCount: wishlistCount } = useWishlist()
  useEffect(() => {
    let active = true
    let revision = 0
    async function refresh() {
      const current = ++revision
      try {
        const count = user?.role === 'CUSTOMER' ? (await getCustomerCart()).totalQuantity : readGuestCart().reduce((sum, item) => sum + item.quantity, 0)
        if (active && current === revision) setCartCount(count)
      } catch { if (active && current === revision) setCartCount(null) }
    }
    void refresh()
    window.addEventListener('hcc:cart-changed', refresh)
    window.addEventListener('storage', refresh)
    return () => { active = false; window.removeEventListener('hcc:cart-changed', refresh); window.removeEventListener('storage', refresh) }
  }, [user?.id, user?.role])
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
      <CatalogSearchBox ariaLabel="Tìm kiếm toàn cửa hàng" className="shop-search" />
      <div className="shop-header-tools">
        {(!user || user.role === 'CUSTOMER') && (
          <NavLink to="/wishlist" className="shop-tool shop-wishlist" aria-label="Yêu thích">
            <HeaderIcon kind="heart" />
            <span className="shop-tool-copy"><small>Yêu thích</small><strong>Đã lưu</strong></span>
            {wishlistCount > 0 && <span className="cart-count wishlist-count" aria-label={`${wishlistCount} sản phẩm yêu thích`}>{wishlistCount > 99 ? '99+' : wishlistCount}</span>}
          </NavLink>
        )}
        {(!user || user.role === 'CUSTOMER') && (
          <NavLink to="/orders" className="shop-tool shop-orders" aria-label="Đơn hàng">
            <HeaderIcon kind="orders" />
            <span className="shop-tool-copy"><small>Tra cứu</small><strong>Đơn hàng</strong></span>
          </NavLink>
        )}
        {user ? <div className="shop-account" ref={container} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false) }}>
          <button type="button" ref={trigger} className="shop-tool" aria-expanded={open} aria-controls="shop-account-panel" aria-label="Tài khoản của bạn" onClick={() => setOpen(value => !value)}>
            <HeaderIcon kind="user" /><span className="shop-tool-copy"><small>Tài khoản</small><strong>{user.fullName}</strong></span><span className="shop-chevron"><HeaderIcon kind="chevron" /></span>
          </button>
          {open && <div id="shop-account-panel" className="shop-account-panel">
            <div className="shop-account-greeting"><strong>{user.fullName}</strong><span>{user.role === 'CUSTOMER' ? 'Tài khoản mua sắm' : user.role === 'STAFF' ? 'Nhân viên vận hành' : 'Quản trị viên'}</span></div>
            <nav aria-label="Tài khoản">
              {user.role === 'CUSTOMER' && <NavLink to="/account" onClick={() => setOpen(false)}>Hồ sơ & địa chỉ</NavLink>}
              {user.role === 'CUSTOMER' && <NavLink to="/orders" onClick={() => setOpen(false)}>Đơn hàng của tôi</NavLink>}
              {user.role === 'CUSTOMER' && <NavLink to="/wishlist" onClick={() => setOpen(false)}>Sản phẩm yêu thích</NavLink>}
              <NavLink to="/notifications" onClick={() => setOpen(false)}>Thông báo</NavLink>
              <NavLink to="/account/security" onClick={() => setOpen(false)}>Bảo mật</NavLink>
            </nav>
            <Form method="post" action="/logout"><button type="submit" disabled={navigation.state !== 'idle'}>Đăng xuất</button></Form>
          </div>}
        </div> : <NavLink to="/login" className="shop-tool shop-login" aria-label="Đăng nhập"><HeaderIcon kind="user" /><span className="shop-tool-copy"><small>Khách hàng</small><strong>Đăng nhập</strong></span></NavLink>}
        <NavLink to="/cart" className="shop-tool shop-cart" aria-label="Mở giỏ hàng"><HeaderIcon kind="cart" /><span className="shop-tool-copy"><small>Túi đồ</small><strong>Giỏ hàng</strong></span>{cartCount !== null && cartCount > 0 && <span className="cart-count" aria-label={`${cartCount} sản phẩm`}>{cartCount > 99 ? '99+' : cartCount}</span>}</NavLink>
      </div>
    </div>
    <div className="shop-nav-row">
      <nav aria-label="Điều hướng danh mục" className="shop-nav">
        <NavLink to="/" end className="shop-nav-item">
          <span>Trang chủ</span>
        </NavLink>
        <Link to="/?categoryId=b36e4c74-b4ed-59d1-bbb5-4b63f2fdce07#catalog" className="shop-nav-item">
          <span>Vợt cầu lông</span>
        </Link>
        <Link to="/?categoryId=980d2aa4-9e78-5fb7-be8d-dc62e03d4f7a#catalog" className="shop-nav-item">
          <span>Giày cầu lông</span>
        </Link>
        <Link to="/?categoryId=d857344c-05a6-5545-81fc-d865c6e7fa4a#catalog" className="shop-nav-item">
          <span>Trang phục</span>
        </Link>
        <Link to="/?categoryId=90592308-fb25-56c4-a362-f6129e93bbf6#catalog" className="shop-nav-item">
          <span>Phụ kiện & Cầu</span>
        </Link>
        <a href="/#catalog" className="shop-nav-item shop-nav-deal">
          <span className="deal-badge" aria-hidden="true">HOT</span>
          <span>Flash Sale</span>
        </a>
        {user?.role === 'STAFF' && <NavLink to="/staff" className="shop-workspace">🛠️ Vận hành</NavLink>}
        {user?.role === 'ADMIN' && <NavLink to="/admin" className="shop-workspace">⚡ Quản trị</NavLink>}
      </nav>
      <div className="shop-nav-trust" aria-label="Cam kết dịch vụ">
        <span className="trust-item">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>
          100% Chính hãng
        </span>
        <span className="trust-sep" aria-hidden="true">•</span>
        <span className="trust-item">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M1 4v6h6"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/></svg>
          Đổi size 7 ngày
        </span>
        <span className="trust-sep" aria-hidden="true">•</span>
        <span className="trust-item">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><rect x="1" y="3" width="15" height="13"/><polygon points="16 8 20 8 23 11 23 16 16 16 16 8"/><circle cx="5.5" cy="18.5" r="2.5"/><circle cx="18.5" cy="18.5" r="2.5"/></svg>
          Giao toàn quốc
        </span>
      </div>
    </div>
  </header>
}
