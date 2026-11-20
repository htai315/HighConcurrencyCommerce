import type { RouteObject } from 'react-router-dom'
import { Outlet } from 'react-router-dom'
import { AppShell } from './components/layout/AppShell'
import { CatalogErrorPage } from './pages/CatalogErrorPage'
import { catalogLoader, productLoader } from './features/catalog/catalog.api'
import { sessionLoader, requireRole, loginAction, registerAction, logoutAction, passwordAction } from './features/auth/auth.api'
import { AuthErrorPage } from './pages/AuthErrorPage'
import { accountLoader, accountAction } from './features/account/account.api'
import { staffLoader, adminLoader } from './features/product-management/product-management.api'
import { cartLoader } from './features/cart/cart.api'
import { notificationLoader } from './features/notifications/notification.api'
import { ordersLoader } from './features/orders/orders.api'
import { wishlistLoader } from './features/wishlist/wishlist.api'
import { customerReturnsLoader } from './features/returns/returns.api'
import { comparisonLoader } from './features/comparison/comparison.api'
import { payOsReturnLoader } from './features/payments/payos.api'

export const appRoutes: RouteObject[] = [
  { id: 'session', loader: sessionLoader, shouldRevalidate: () => true, element: <Outlet />, errorElement: <AuthErrorPage />,
    hydrateFallbackElement: <p className="content-page" role="status">Đang tải cửa hàng…</p>, children: [
  { element: <AppShell />, hydrateFallbackElement: <p className="content-page" role="status">Đang tải cửa hàng…</p>, children: [
    { index: true, loader: catalogLoader, lazy: async () => ({ Component: (await import('./pages/StorefrontPage')).StorefrontPage }), errorElement: <CatalogErrorPage /> },
    { path: 'products/:id', loader: productLoader, lazy: async () => ({ Component: (await import('./pages/ProductDetailPage')).ProductDetailPage }), errorElement: <CatalogErrorPage /> },
    { path: 'compare', loader: comparisonLoader, lazy: async () => ({ Component: (await import('./pages/ComparePage')).ComparePage }), errorElement: <CatalogErrorPage /> },
    { path: 'cart', loader: cartLoader, lazy: async () => ({ Component: (await import('./pages/CartPage')).CartPage }), errorElement: <AuthErrorPage /> },
    { path: 'wishlist', loader: wishlistLoader, lazy: async () => ({ Component: (await import('./pages/WishlistPage')).WishlistPage }), errorElement: <AuthErrorPage /> },
    { path: 'orders', loader: ordersLoader, lazy: async () => ({ Component: (await import('./pages/OrdersPage')).OrdersPage }), errorElement: <AuthErrorPage /> },
    { path: 'payment/payos/return', loader: payOsReturnLoader, lazy: async () => ({ Component: (await import('./pages/PayOsReturnPage')).PayOsReturnPage }), errorElement: <AuthErrorPage /> },
    { path: 'account/returns', loader: customerReturnsLoader, lazy: async () => ({ Component: (await import('./pages/CustomerReturnsPage')).CustomerReturnsPage }), errorElement: <AuthErrorPage /> },
    { path: 'staff', loader: staffLoader, lazy: async () => ({ Component: (await import('./pages/StaffDashboardPage')).StaffDashboardPage }), errorElement: <AuthErrorPage /> },
    { path: 'admin', loader: adminLoader, lazy: async () => ({ Component: (await import('./pages/AdminDashboardPage')).AdminDashboardPage }), errorElement: <AuthErrorPage /> },
    { path: 'account', loader: accountLoader, action: accountAction, lazy: async () => ({ Component: (await import('./pages/AccountPage')).AccountPage }), errorElement: <AuthErrorPage /> },
    { path: 'account/security', loader: requireRole(['CUSTOMER', 'STAFF', 'ADMIN']), action: passwordAction, lazy: async () => ({ Component: (await import('./pages/PasswordPage')).PasswordPage }), errorElement: <AuthErrorPage /> },
    { path: 'notifications', loader: notificationLoader, lazy: async () => ({ Component: (await import('./pages/NotificationsPage')).NotificationsPage }), errorElement: <AuthErrorPage /> },
    { path: 'logout', action: logoutAction, errorElement: <AuthErrorPage /> },
  ] },
  { path: 'login', action: loginAction, lazy: async () => ({ Component: (await import('./pages/LoginPage')).LoginPage }) },
  { path: 'register', action: registerAction, lazy: async () => ({ Component: (await import('./pages/RegisterPage')).RegisterPage }) },
  { path: 'forgot-password', lazy: async () => ({ Component: (await import('./pages/ForgotPasswordPage')).ForgotPasswordPage }) },
  { path: 'reset-password', lazy: async () => ({ Component: (await import('./pages/ResetPasswordPage')).ResetPasswordPage }) },
  { path: 'staff-invitation', lazy: async () => ({ Component: (await import('./pages/StaffInvitationPage')).StaffInvitationPage }) },
  { path: 'verify-email', lazy: async () => ({ Component: (await import('./pages/VerifyEmailPage')).VerifyEmailPage }) },
  { path: '*', lazy: async () => ({ Component: (await import('./pages/NotFoundPage')).NotFoundPage }) },
  ] },
]
