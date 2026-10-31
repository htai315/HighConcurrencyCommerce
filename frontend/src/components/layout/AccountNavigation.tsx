import { NavLink } from 'react-router-dom'

export function AccountNavigation() {
  return <nav className="account-navigation" aria-label="Không gian tài khoản">
    {([['/account', 'Hồ sơ & địa chỉ'], ['/orders', 'Đơn hàng'], ['/notifications', 'Thông báo'], ['/account/security', 'Bảo mật']] as const).map(([to, label]) =>
      <NavLink key={to} to={to} end>{label}</NavLink>)}
  </nav>
}
