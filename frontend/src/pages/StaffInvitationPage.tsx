import { useState, useEffect } from 'react'
import type { FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { AuthLayout } from '../components/layout/AuthLayout'
import { PasswordField } from '../features/auth/PasswordField'
import { PasswordChecklist } from '../features/auth/PasswordChecklist'
import { passwordError } from '../features/auth/password-policy'
import { acceptStaffInvitation, adminAccountErrorMessage } from '../features/admin-accounts/admin-accounts.api'

function extractTokenFromHash(): string {
  const hash = window.location.hash.replace(/^#/, '')
  const params = new URLSearchParams(hash)
  return params.get('token') || ''
}

export function StaffInvitationPage() {
  const navigate = useNavigate()
  const [token] = useState(() => extractTokenFromHash())
  const tokenMissing = !token
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (window.location.hash) {
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
      await acceptStaffInvitation({ token, password, confirmPassword: confirmation })
      navigate('/login?staffInvited=1')
    } catch (err) {
      setError(adminAccountErrorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout>
      <form onSubmit={handleSubmit} className="court-form">
        <div className="auth-tabs" aria-label="Tài khoản">
          <Link to="/login">Đăng nhập</Link>
          <span aria-current="page">Kích hoạt nhân sự</span>
        </div>
        <h1>Kích hoạt tài khoản nhân sự</h1>
        <p className="court-form-intro">
          Chào mừng bạn gia nhập đội ngũ vận hành. Vui lòng thiết lập mật khẩu để kích hoạt tài khoản của bạn.
        </p>

        {tokenMissing ? (
          <div role="alert" className="auth-notice error" style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
            <span>Liên kết kích hoạt không hợp lệ hoặc thiếu mã bảo mật. Vui lòng liên hệ Quản trị viên để nhận liên kết mới.</span>
            <Link to="/login" className="court-submit" style={{ display: 'inline-block', textAlign: 'center', textDecoration: 'none' }}>
              Trở về trang đăng nhập
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
              name="password"
              label="Mật khẩu mới"
              value={password}
              onChange={setPassword}
              describedBy="staff-password-guide"
              autoComplete="new-password"
            />
            <PasswordChecklist password={password} id="staff-password-guide" />
            <PasswordField
              name="confirmPassword"
              label="Nhập lại mật khẩu"
              value={confirmation}
              onChange={setConfirmation}
              invalid={mismatch}
              describedBy="staff-password-match"
              autoComplete="new-password"
            />
            <p id="staff-password-match" className={mismatch ? 'password-match mismatch' : 'password-match'} aria-live="polite">
              {confirmation ? (mismatch ? 'Mật khẩu chưa khớp.' : 'Mật khẩu đã khớp.') : ''}
            </p>

            <button
              type="submit"
              className="court-submit"
              disabled={busy || mismatch || !password || !confirmation}
            >
              {busy ? 'Đang kích hoạt tài khoản…' : 'Kích hoạt tài khoản'}<span aria-hidden="true">↗</span>
            </button>
          </>
        )}

        <p className="court-switch">
          Đã có tài khoản? <Link to="/login">Đăng nhập ngay</Link>
        </p>
      </form>
    </AuthLayout>
  )
}

