import { useState } from 'react'
import type { FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { AuthInput } from '../features/auth/AuthInput'
import { errorMessage, requestPasswordReset } from '../features/auth/auth.api'

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!email.trim() || busy) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const res = await requestPasswordReset(email.trim())
      setNotice(res.message)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout>
      <form onSubmit={handleSubmit} className="court-form">
        <div className="auth-tabs" aria-label="Tài khoản">
          <Link to="/login">Đăng nhập</Link>
          <Link to="/register">Đăng ký</Link>
        </div>
        <h1>Quên mật khẩu</h1>
        <p className="court-form-intro">
          Nhập địa chỉ email bạn đã dùng để đăng ký tài khoản. Chúng tôi sẽ gửi hướng dẫn đặt lại mật khẩu cho bạn.
        </p>

        {notice && (
          <p role="status" className="auth-notice" style={{ background: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
            {notice}
          </p>
        )}

        {error && (
          <p role="alert" className="auth-notice error">
            {error}
          </p>
        )}

        {!notice ? (
          <>
            <AuthInput
              label="Email"
              name="email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              autoComplete="email"
              maxLength={320}
              placeholder="ban@email.com"
              required
            />
            <button type="submit" className="court-submit" disabled={busy || !email.trim()}>
              {busy ? 'Đang gửi yêu cầu…' : 'Gửi liên kết đặt lại mật khẩu'}<span aria-hidden="true">↗</span>
            </button>
          </>
        ) : (
          <div style={{ textAlign: 'center', marginTop: '1rem' }}>
            <Link to="/login" className="court-submit" style={{ display: 'inline-block', textDecoration: 'none' }}>
              Quay lại đăng nhập
            </Link>
          </div>
        )}

        <p className="court-switch">
          Nhớ mật khẩu rồi? <Link to="/login">Đăng nhập ngay</Link>
        </p>
      </form>
    </AuthLayout>
  )
}
