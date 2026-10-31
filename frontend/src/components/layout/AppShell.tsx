import { Outlet, useLocation, useNavigation, useRouteLoaderData } from 'react-router-dom'
import './storefront-premium.css'
import { AccountNavigation } from './AccountNavigation'
import { SiteHeader } from './SiteHeader'
import { sessionLoader } from '../../features/auth/auth.api'

const currentYear = new Date().getFullYear()

function ShuttleMark() {
  return <svg viewBox="0 0 32 32" aria-hidden="true"><path d="m8 5 7 14h3L25 5M12 5l5 14M21 5l-5 14M12 23c1 3 7 3 8 0" fill="none" stroke="currentColor" strokeWidth="2.3" strokeLinecap="round" strokeLinejoin="round"/></svg>
}

export function AppShell() {
  const navigation = useNavigation()
  const user = useRouteLoaderData<typeof sessionLoader>('session')
  const pathname = useLocation().pathname
  const accountArea = ['/account', '/account/security', '/orders', '/notifications'].includes(pathname)

  return (
    <div className="app-frame storefront-premium commerce-premium">
      <a className="skip-link" href="#main-content">Đến nội dung chính</a>
      <SiteHeader key={pathname} user={user} />

      <main className="page-main" id="main-content" tabIndex={-1}>
        {navigation.state !== 'idle' && (
          <p className="navigation-progress" role="status">
            Đang tải…
          </p>
        )}
        {accountArea && user?.role === 'CUSTOMER' && <AccountNavigation />}
        <Outlet />
      </main>

      <footer className="site-footer">
        <div className="footer-brand">
          <span className="brand-mark"><ShuttleMark /></span>
          <div><strong>HIGHCOMMERCE</strong><p>Trang bị đúng. Chơi hết mình.</p></div>
        </div>
        <div className="footer-promise">
          <span>Vợt · Giày · Trang phục · Cầu & phụ kiện</span>
          <p>© {currentYear} HighCommerce. Bảo lưu mọi quyền.</p>
        </div>
      </footer>
    </div>
  )
}
