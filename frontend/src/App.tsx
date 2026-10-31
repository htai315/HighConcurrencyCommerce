import type { RouteObject } from 'react-router-dom'
import { Outlet } from 'react-router-dom'
import { AppShell } from './components/layout/AppShell'
import { AdminDashboardPage } from './pages/AdminDashboardPage'
import { CartPage } from './pages/CartPage'
import { LoginPage } from './pages/LoginPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { OrdersPage } from './pages/OrdersPage'
import { StaffDashboardPage } from './pages/StaffDashboardPage'
import { StorefrontPage } from './pages/StorefrontPage'
import { CatalogErrorPage } from './pages/CatalogErrorPage'
import { ProductDetailPage } from './pages/ProductDetailPage'
import { catalogLoader, productLoader } from './features/catalog/catalog.api'
import { sessionLoader, requireRole, loginAction, registerAction, logoutAction, passwordAction } from './features/auth/auth.api'
import { RegisterPage } from './pages/RegisterPage'
import { PasswordPage } from './pages/PasswordPage'
import { AuthErrorPage } from './pages/AuthErrorPage'
import { AccountPage } from './pages/AccountPage'
import { accountLoader, accountAction } from './features/account/account.api'
import { staffLoader, adminLoader } from './features/product-management/product-management.api'
import { cartLoader } from './features/cart/cart.api'
import { notificationLoader } from './features/notifications/notification.api'
import { NotificationsPage } from './pages/NotificationsPage'
import { ordersLoader } from './features/orders/orders.api'

export const appRoutes: RouteObject[] = [
  { id: 'session', loader: sessionLoader, shouldRevalidate: () => true, element: <Outlet />, errorElement: <AuthErrorPage />,
    hydrateFallbackElement: <p className="content-page" role="status">Đang tải cửa hàng…</p>, children: [
  { element: <AppShell />, hydrateFallbackElement: <p className="content-page" role="status">Đang tải cửa hàng…</p>, children: [
    { index: true, loader: catalogLoader, element: <StorefrontPage />, errorElement: <CatalogErrorPage /> },
    { path: 'products/:id', loader: productLoader, element: <ProductDetailPage />, errorElement: <CatalogErrorPage /> },
    { path: 'cart', loader: cartLoader, element: <CartPage />, errorElement: <AuthErrorPage /> },
    { path: 'orders', loader: ordersLoader, element: <OrdersPage />, errorElement: <AuthErrorPage /> },
    { path: 'staff', loader: staffLoader, element: <StaffDashboardPage />, errorElement: <AuthErrorPage /> },
    { path: 'admin', loader: adminLoader, element: <AdminDashboardPage />, errorElement: <AuthErrorPage /> },
    { path: 'account', loader: accountLoader, action: accountAction, element: <AccountPage />, errorElement: <AuthErrorPage /> },
    { path: 'account/security', loader: requireRole(['CUSTOMER', 'STAFF', 'ADMIN']), action: passwordAction, element: <PasswordPage />, errorElement: <AuthErrorPage /> },
    { path: 'notifications', loader: notificationLoader, element: <NotificationsPage />, errorElement: <AuthErrorPage /> },
    { path: 'logout', action: logoutAction, errorElement: <AuthErrorPage /> },
  ] },
  { path: 'login', action: loginAction, element: <LoginPage /> },
  { path: 'register', action: registerAction, element: <RegisterPage /> },
  { path: '*', element: <NotFoundPage /> },
  ] },
]
