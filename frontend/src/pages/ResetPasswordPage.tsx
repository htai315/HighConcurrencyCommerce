import { useState, useEffect } from 'react'
import type { FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { PasswordField } from '../features/auth/PasswordField'
import { PasswordChecklist } from '../features/auth/PasswordChecklist'
import { passwordError } from '../features/auth/password-policy'
import { confirmPasswordReset, errorMessage } from '../features/auth/auth.api'

function extractTokenFromHash(): string {
  const hash = window.location.hash.replace(/^#/, '')
  const params = new URLSearchParams(hash)
  return params.get('token') || ''
}

export function ResetPasswordPage() {
  const navigate = useNavigate()
  const [token] = useState(() => extractTokenFromHash())
  const tokenMissing = !token
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (window.location.hash) {
      // Clean fragment from URL immediately to prevent leakage
      window.history.replaceState(null, '', window.location.pathname + window.location.search)
    }
  }, [])

  const mismatch = confirmation.length > 0 && password !== confirmation

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!token || busy) return
    const policyErr = passwordError(password)
    if (policyErr) {
      setError(policyErr)
      return
    }
    if (password !== confirmation) {
      setError('Mật khẩu xác nhận không khớp.')
      return
    }

    setBusy(true)
    setError(null)
    try {
      await confirmPasswordReset({ token, newPassword: password, confirmPassword: confirmation })
      navigate('/login?reset=1')
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
        <h1>Đặt lại mật khẩu</h1>
        <p className="court-form-intro">
          Nhập mật khẩu mới cho tài khoản của bạn. Mật khẩu phải đáp ứng các tiêu chuẩn bảo mật.
        </p>

        {tokenMissing ? (
          <div role="alert" className="auth-notice error" style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
            <span>Liên kết đặt lại mật khẩu không hợp lệ hoặc thiếu mã xác thực. Vui lòng yêu cầu liên kết mới.</span>
            <Link to="/forgot-password" className="court-submit" style={{ display: 'inline-block', textAlign: 'center', textDecoration: 'none' }}>
              Yêu cầu liên kết mới
            </Link>
          </div>
        ) : (
          <>
            {error && (
              <p role="alert" className="auth-notice error">
                {error}
              </p>
            )}

            <PasswordField
              name="newPassword"
              label="Mật khẩu mới"
              value={password}
              onChange={setPassword}
              describedBy="password-guide"
            />
            <PasswordChecklist password={password} id="password-guide" />
            <PasswordField
              name="confirmPassword"
              label="Nhập lại mật khẩu mới"
              value={confirmation}
              onChange={setConfirmation}
              invalid={mismatch}
              describedBy="password-match"
            />
            <p id="password-match" className={mismatch ? 'password-match mismatch' : 'password-match'} aria-live="polite">
              {confirmation ? mismatch ? 'Mật khẩu chưa khớp.' : 'Mật khẩu đã khớp.' : ''}
            </p>

            <button type="submit" className="court-submit" disabled={busy || mismatch || !password || !confirmation}>
              {busy ? 'Đang cập nhật mật khẩu…' : 'Lưu mật khẩu mới'}<span aria-hidden="true">↗</span>
            </button>
          </>
        )}

        <p className="court-switch">
          Nhớ mật khẩu rồi? <Link to="/login">Đăng nhập ngay</Link>
        </p>
      </form>
    </AuthLayout>
  )
}
