import { useState, useEffect } from 'react'
import type { FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { AuthInput } from '../features/auth/AuthInput'
import { confirmEmailVerification, resendVerificationEmail, errorMessage, getPendingVerificationEmail } from '../features/auth/auth.api'

function extractTokenFromHash(): string {
  const hash = window.location.hash.replace(/^#/, '')
  const params = new URLSearchParams(hash)
  return params.get('token') || ''
}

export function VerifyEmailPage() {
  const [searchParams] = useSearchParams()
  const pendingEmail = getPendingVerificationEmail() || searchParams.get('email') || ''
  const isRegisteredNotice = searchParams.get('registered') === '1'

  const [initialToken] = useState(() => extractTokenFromHash())
  const [verifying, setVerifying] = useState(() => Boolean(extractTokenFromHash()))
  const [verifiedSuccess, setVerifiedSuccess] = useState(false)
  const [verifyError, setVerifyError] = useState<string | null>(null)

  const [resendEmail, setResendEmail] = useState(pendingEmail)
  const [resendBusy, setResendBusy] = useState(false)
  const [resendNotice, setResendNotice] = useState<string | null>(null)
  const [resendError, setResendError] = useState<string | null>(null)

  useEffect(() => {
    if (initialToken) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search)
      confirmEmailVerification(initialToken)
        .then(() => {
          setVerifiedSuccess(true)
        })
        .catch((err) => {
          setVerifyError(errorMessage(err))
        })
        .finally(() => {
          setVerifying(false)
        })
    }
  }, [initialToken])

  async function handleResend(e: FormEvent) {
    e.preventDefault()
    if (!resendEmail.trim() || resendBusy) return
    setResendBusy(true)
    setResendError(null)
    setResendNotice(null)
    try {
      const res = await resendVerificationEmail(resendEmail.trim())
      setResendNotice(res.message)
    } catch (err) {
      setResendError(errorMessage(err))
    } finally {
      setResendBusy(false)
    }
  }

  return (
    <AuthLayout>
      <div className="court-form">
        <div className="auth-tabs" aria-label="Tài khoản">
          <Link to="/login">Đăng nhập</Link>
          <Link to="/register">Đăng ký</Link>
        </div>

        {verifying ? (
          <div style={{ textAlign: 'center', padding: '2rem 0' }}>
            <h1>Đang xác minh email…</h1>
            <p className="court-form-intro">Vui lòng chờ trong giây lát, hệ thống đang kích hoạt tài khoản của bạn.</p>
          </div>
        ) : verifiedSuccess ? (
          <div style={{ textAlign: 'center', padding: '1rem 0' }}>
            <h1 style={{ color: '#16a34a' }}>Xác minh thành công!</h1>
            <p className="court-form-intro">
              Địa chỉ email của bạn đã được xác minh. Tài khoản hiện đã sẵn sàng để đăng nhập và mua sắm.
            </p>
            <div style={{ marginTop: '1.5rem' }}>
              <Link to="/login?verified=1" className="court-submit" style={{ display: 'inline-block', textDecoration: 'none' }}>
                Đăng nhập ngay<span aria-hidden="true">↗</span>
              </Link>
            </div>
          </div>
        ) : verifyError ? (
          <div>
            <h1>Xác minh không thành công</h1>
            <p role="alert" className="auth-notice error" style={{ margin: '1rem 0' }}>
              {verifyError}
            </p>
            <p className="court-form-intro">
              Liên kết có thể đã hết hạn (24 giờ) hoặc đã được sử dụng. Bạn có thể yêu cầu gửi lại email xác minh bên dưới.
            </p>

            <form onSubmit={handleResend} style={{ marginTop: '1.5rem' }}>
              {resendNotice && (
                <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
                  {resendNotice}
                </p>
              )}
              {resendError && (
                <p role="alert" className="auth-notice error">
                  {resendError}
                </p>
              )}
              <AuthInput
                label="Email"
                name="email"
                type="email"
                value={resendEmail}
                onChange={(e) => setResendEmail(e.target.value)}
                autoComplete="email"
                maxLength={320}
                placeholder="ban@email.com"
                required
              />
              <button type="submit" className="court-submit" disabled={resendBusy || !resendEmail.trim()}>
                {resendBusy ? 'Đang gửi lại…' : 'Gửi lại email xác minh'}<span aria-hidden="true">↗</span>
              </button>
            </form>
          </div>
        ) : isRegisteredNotice ? (
          <div>
            <h1>Kiểm tra hộp thư của bạn</h1>
            <p className="court-form-intro">
              Tài khoản đã được tạo thành công! Chúng tôi đã gửi một liên kết kích hoạt đến địa chỉ email:
            </p>
            {pendingEmail && (
              <p style={{ fontWeight: 600, fontSize: '1.1rem', color: '#111827', margin: '0.5rem 0 1rem' }}>
                {pendingEmail}
              </p>
            )}
            <p style={{ color: '#4b5563', fontSize: '0.925rem', lineHeight: 1.5 }}>
              Vui lòng kiểm tra hộp thư đến (và thư mục rác / spam nếu cần) và bấm vào liên kết trong email để kích hoạt tài khoản của bạn.
            </p>

            <form onSubmit={handleResend} style={{ marginTop: '1.5rem', borderTop: '1px solid #e5e7eb', paddingTop: '1.5rem' }}>
              <p style={{ fontSize: '0.875rem', color: '#6b7280', marginBottom: '0.75rem' }}>
                Chưa nhận được email? Nhập email để yêu cầu gửi lại:
              </p>
              {resendNotice && (
                <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
                  {resendNotice}
                </p>
              )}
              {resendError && (
                <p role="alert" className="auth-notice error">
                  {resendError}
                </p>
              )}
              <AuthInput
                label="Email"
                name="email"
                type="email"
                value={resendEmail}
                onChange={(e) => setResendEmail(e.target.value)}
                autoComplete="email"
                maxLength={320}
                placeholder="ban@email.com"
                required
              />
              <button type="submit" className="court-submit" disabled={resendBusy || !resendEmail.trim()}>
                {resendBusy ? 'Đang gửi lại…' : 'Gửi lại email xác minh'}<span aria-hidden="true">↗</span>
              </button>
            </form>
          </div>
        ) : (
          <div>
            <h1>Xác minh địa chỉ email</h1>
            <p className="court-form-intro">
              Nhập email đã đăng ký để nhận lại liên kết xác minh tài khoản.
            </p>
            <form onSubmit={handleResend} style={{ marginTop: '1rem' }}>
              {resendNotice && (
                <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
                  {resendNotice}
                </p>
              )}
              {resendError && (
                <p role="alert" className="auth-notice error">
                  {resendError}
                </p>
              )}
              <AuthInput
                label="Email"
                name="email"
                type="email"
                value={resendEmail}
                onChange={(e) => setResendEmail(e.target.value)}
                autoComplete="email"
                maxLength={320}
                placeholder="ban@email.com"
                required
              />
              <button type="submit" className="court-submit" disabled={resendBusy || !resendEmail.trim()}>
                {resendBusy ? 'Đang gửi lại…' : 'Gửi lại email xác minh'}<span aria-hidden="true">↗</span>
              </button>
            </form>
          </div>
        )}

        <p className="court-switch" style={{ marginTop: '2rem' }}>
          Đã xác minh? <Link to="/login">Đăng nhập ngay</Link>
        </p>
      </div>
    </AuthLayout>
  )
}
