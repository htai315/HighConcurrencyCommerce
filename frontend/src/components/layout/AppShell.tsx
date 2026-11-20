import { Form, Link, Outlet, useLocation, useNavigation, useRouteLoaderData } from 'react-router-dom'
import './storefront-premium.css'
import { AccountNavigation } from './AccountNavigation'
import { SiteHeader } from './SiteHeader'
import { sessionLoader } from '../../features/auth/auth.api'
import { WishlistProvider } from '../../features/wishlist/WishlistContext'
import { ComparisonProvider } from '../../features/comparison/ComparisonContext'
import { ComparisonTray } from '../../features/comparison/ComparisonTray'
import { RecentProductsProvider } from '../../features/recent-products/RecentProductsContext'
import '../../features/recent-products/discovery.css'

const currentYear = new Date().getFullYear()

function ShuttleMark() {
  return <svg viewBox="0 0 32 32" aria-hidden="true"><path d="m8 5 7 14h3L25 5M12 5l5 14M21 5l-5 14M12 23c1 3 7 3 8 0" fill="none" stroke="currentColor" strokeWidth="2.3" strokeLinecap="round" strokeLinejoin="round"/></svg>
}

export function AppShell() {
  const navigation = useNavigation()
  const user = useRouteLoaderData<typeof sessionLoader>('session')
  const pathname = useLocation().pathname
  const accountArea = ['/account', '/account/security', '/orders', '/notifications', '/wishlist', '/account/returns'].includes(pathname)
  const workspace = pathname === '/staff' || pathname === '/admin'
  const customerArea = accountArea && user?.role === 'CUSTOMER'

  return (
    <RecentProductsProvider>
      <ComparisonProvider>
        <WishlistProvider user={user}>
      <div className={`app-frame storefront-premium commerce-premium${workspace ? ' operations-shell' : ''}`}>
        <a className="skip-link" href="#main-content">Đến nội dung chính</a>
        {workspace ? <header className="operations-header">
          <Link className="operations-brand" to="/">HighCommerce <span>{user?.role === 'ADMIN' ? 'Quản trị' : 'Vận hành'}</span></Link>
          <nav aria-label="Tài khoản vận hành"><Link to="/">Về cửa hàng</Link><Link to="/notifications">Thông báo</Link><Link to="/account/security">Bảo mật</Link><Form method="post" action="/logout"><button type="submit" disabled={navigation.state !== 'idle'}>Đăng xuất</button></Form></nav>
        </header> : <SiteHeader key={pathname} user={user} />}

        <main className="page-main" id="main-content" tabIndex={-1}>
          {navigation.state !== 'idle' && (
            <p className="navigation-progress" role="status">
              Đang tải…
            </p>
          )}
          {customerArea ? <div className="customer-space"><AccountNavigation /><div className="customer-space-content"><Outlet /></div></div> : <Outlet />}
        </main>

        <ComparisonTray />

        {workspace ? <footer className="operations-footer">HighCommerce · Không gian làm việc</footer> : <footer className="site-footer">
          <div className="footer-brand">
            <span className="brand-mark"><ShuttleMark /></span>
            <div><strong>HIGHCOMMERCE</strong><p>Trang bị đúng. Chơi hết mình.</p></div>
          </div>
          <div className="footer-promise">
            <span>Vợt · Giày · Trang phục · Cầu & phụ kiện</span>
            <p>© {currentYear} HighCommerce. Bảo lưu mọi quyền.</p>
          </div>
          <nav className="footer-links" aria-label="Liên kết cuối trang"><Link to="/#catalog">Sản phẩm</Link><Link to="/orders">Theo dõi đơn hàng</Link><Link to="/account">Tài khoản</Link></nav>
        </footer>}
      </div>
        </WishlistProvider>
      </ComparisonProvider>
    </RecentProductsProvider>
  )
}
