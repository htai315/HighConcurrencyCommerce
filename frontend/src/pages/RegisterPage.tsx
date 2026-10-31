import { useState } from 'react'
import { Form, Link, useActionData, useNavigation } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { registerAction } from '../features/auth/auth.api'
import { PasswordField } from '../features/auth/PasswordField'
import { PasswordChecklist } from '../features/auth/PasswordChecklist'
import { AuthInput } from '../features/auth/AuthInput'

export function RegisterPage() {
  const result = useActionData<typeof registerAction>()
  const busy = useNavigation().state !== 'idle'
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const mismatch = confirmation.length > 0 && password !== confirmation
  return <AuthLayout><Form method="post" className="court-form">
    <div className="auth-tabs" aria-label="Tài khoản"><Link to="/login">Đăng nhập</Link><Link to="/register" aria-current="page">Đăng ký</Link></div>
    <h1>Tạo tài khoản mới</h1><p className="court-form-intro">Tìm vợt, giày, trang phục và phụ kiện cho buổi cầu tiếp theo. Bắt đầu cùng HighCommerce.</p>
    {result?.error && <p role="alert" className="auth-notice error">{result.error}</p>}
    <AuthInput label="Họ và tên" name="fullName" autoComplete="name" maxLength={160} placeholder="Nguyễn Minh Anh" required />
    <AuthInput label="Email" name="email" type="email" autoComplete="email" maxLength={320} placeholder="ban@email.com" required />
    <AuthInput label="Số điện thoại" name="phone" type="tel" placeholder="Thêm trong hồ sơ sau khi đăng ký" disabled hint="Bạn có thể cập nhật số điện thoại tại trang Hồ sơ." />
    <PasswordField name="password" label="Mật khẩu" value={password} onChange={setPassword} describedBy="password-guide" />
    <PasswordChecklist password={password} id="password-guide" />
    <PasswordField name="confirmPassword" label="Nhập lại mật khẩu" value={confirmation} onChange={setConfirmation} invalid={mismatch} describedBy="password-match" />
    <p id="password-match" className={mismatch ? 'password-match mismatch' : 'password-match'} aria-live="polite">{confirmation ? mismatch ? 'Mật khẩu chưa khớp.' : 'Mật khẩu đã khớp.' : ''}</p>
    <label className="auth-consent"><input type="checkbox" required /> <span>Tôi đồng ý tạo tài khoản và cung cấp thông tin trên để quản lý hồ sơ, đơn hàng của mình.</span></label>
    <button type="submit" className="court-submit" disabled={busy}>{busy ? 'Đang tạo tài khoản…' : 'Tạo tài khoản'}<span aria-hidden="true">↗</span></button>
    <p className="court-switch">Đã có tài khoản? <Link to="/login">Đăng nhập ngay</Link></p>
  </Form></AuthLayout>
}

