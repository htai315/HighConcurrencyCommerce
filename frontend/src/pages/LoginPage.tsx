import { useState } from 'react'
import { Form, Link, useActionData, useNavigation, useSearchParams } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { loginAction, setPendingVerificationEmail } from '../features/auth/auth.api'
import { PasswordField } from '../features/auth/PasswordField'
import { AuthInput } from '../features/auth/AuthInput'
import { GoogleSignInButton } from '../features/auth/GoogleSignInButton'

export function LoginPage() {
  const result = useActionData<typeof loginAction>()
  const busy = useNavigation().state !== 'idle'
  const [query] = useSearchParams()
  const [googleError, setGoogleError] = useState<string | null>(null)

  return (
    <AuthLayout>
      <Form method="post" className="court-form">
        <div className="auth-tabs" aria-label="Tài khoản">
          <Link to="/login" aria-current="page">Đăng nhập</Link>
          <Link to="/register">Đăng ký</Link>
        </div>
        <h1>Chào mừng trở lại</h1>
        <p className="court-form-intro">Đăng nhập để tiếp tục mua sắm dụng cụ cầu lông yêu thích của bạn.</p>
        {query.has('registered') && (
          <p role="status" className="auth-notice">
            Bạn có thể đăng nhập bằng email và mật khẩu đã chọn. Nếu đã có tài khoản, dùng mật khẩu hiện tại.
          </p>
        )}
        {query.has('verified') && (
          <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
            Xác minh email thành công! Bạn có thể đăng nhập ngay.
          </p>
        )}
        {query.has('reset') && (
          <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
            Đặt lại mật khẩu thành công! Vui lòng đăng nhập bằng mật khẩu mới.
          </p>
        )}
        {query.has('staffInvited') && (
          <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
            Tài khoản nhân sự đã kích hoạt thành công. Vui lòng đăng nhập bằng mật khẩu vừa tạo.
          </p>
        )}
        {query.has('passwordChanged') && (
          <p role="status" className="auth-notice">Đã đổi mật khẩu. Vui lòng đăng nhập lại.</p>
        )}
        {result?.code === 'EMAIL_NOT_VERIFIED' ? (
          <div role="alert" className="auth-notice error" style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
            <span>{result.error}</span>
            <Link
              to={`/verify-email?resend=1&email=${encodeURIComponent(result.email || '')}`}
              onClick={() => {
                if (result.email) {
                  setPendingVerificationEmail(result.email)
                }
              }}
              className="court-secondary-btn"
              style={{ display: 'inline-block', textAlign: 'center', marginTop: '0.25rem', padding: '0.4rem 0.8rem', fontSize: '0.875rem', background: '#fee2e2', color: '#991b1b', borderRadius: '4px', textDecoration: 'none', fontWeight: 500 }}
            >
              Gửi lại email xác minh
            </Link>
          </div>
        ) : (result?.error || googleError) ? (
          <p role="alert" className="auth-notice error">{result?.error || googleError}</p>
        ) : null}
        <AuthInput label="Email" name="email" type="email" autoComplete="username" maxLength={320} placeholder="ban@email.com" required />
        <PasswordField name="password" label="Mật khẩu" autoComplete="current-password" />
        <div className="auth-options" style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: '0.75rem' }}>
          <Link to="/forgot-password" className="auth-link" style={{ fontSize: '0.875rem', color: '#2563eb', textDecoration: 'none' }}>
            Quên mật khẩu?
          </Link>
        </div>
        <button type="submit" className="court-submit" disabled={busy}>
          {busy ? 'Đang đăng nhập…' : 'Đăng nhập'}<span aria-hidden="true">↗</span>
        </button>

        <GoogleSignInButton mode="login" onError={setGoogleError} />

        <p className="court-switch">Chưa có tài khoản? <Link to="/register">Đăng ký ngay</Link></p>
        <div className="court-member-note">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
            <path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Z"/>
            <path d="m8 12 3 3 5-6"/>
          </svg>
          <p>Tài khoản của bạn, trải nghiệm của bạn.<span>Thông tin mua sắm được lưu riêng trong tài khoản.</span></p>
        </div>
      </Form>
    </AuthLayout>
  )
}
