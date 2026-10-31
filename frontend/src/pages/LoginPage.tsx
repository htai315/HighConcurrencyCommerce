import { Form, Link, useActionData, useNavigation, useSearchParams } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { loginAction } from '../features/auth/auth.api'
import { PasswordField } from '../features/auth/PasswordField'
import { AuthInput } from '../features/auth/AuthInput'

export function LoginPage() {
  const result = useActionData<typeof loginAction>()
  const busy = useNavigation().state !== 'idle'
  const [query] = useSearchParams()
  return <AuthLayout><Form method="post" className="court-form">
    <div className="auth-tabs" aria-label="Tài khoản"><Link to="/login" aria-current="page">Đăng nhập</Link><Link to="/register">Đăng ký</Link></div>
    <h1>Chào mừng trở lại</h1><p className="court-form-intro">Đăng nhập để tiếp tục mua sắm dụng cụ cầu lông yêu thích của bạn.</p>
    {query.has('registered') && <p role="status" className="auth-notice">Bạn có thể đăng nhập bằng email và mật khẩu đã chọn. Nếu đã có tài khoản, dùng mật khẩu hiện tại.</p>}
    {query.has('passwordChanged') && <p role="status" className="auth-notice">Đã đổi mật khẩu. Vui lòng đăng nhập lại.</p>}
    {result?.error && <p role="alert" className="auth-notice error">{result.error}</p>}
    <AuthInput label="Email" name="email" type="email" autoComplete="username" maxLength={320} placeholder="ban@email.com" required />
    <PasswordField name="password" label="Mật khẩu" autoComplete="current-password" />
    <div className="auth-options"><label title="Chưa hỗ trợ tùy chọn ghi nhớ phiên"><input type="checkbox" disabled /> Ghi nhớ đăng nhập <small>(sắp có)</small></label><details className="auth-recovery"><summary>Quên mật khẩu?</summary><p>Khôi phục mật khẩu trực tuyến chưa khả dụng. Vui lòng liên hệ bộ phận hỗ trợ của cửa hàng.</p></details></div>
    <button type="submit" className="court-submit" disabled={busy}>{busy ? 'Đang đăng nhập…' : 'Đăng nhập'}<span aria-hidden="true">↗</span></button>
    <p className="court-switch">Chưa có tài khoản? <Link to="/register">Đăng ký ngay</Link></p>
    <div className="auth-social"><p>Đăng nhập nhanh <span>· Sắp ra mắt</span></p><div><button type="button" disabled><b aria-hidden="true">G</b> Google</button><button type="button" disabled><b aria-hidden="true">f</b> Facebook</button></div></div>
    <div className="court-member-note"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true"><path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Z"/><path d="m8 12 3 3 5-6"/></svg><p>Tài khoản của bạn, trải nghiệm của bạn.<span>Thông tin mua sắm được lưu riêng trong tài khoản.</span></p></div>
  </Form></AuthLayout>
}

