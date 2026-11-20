import { Form, useActionData, useNavigation, useLoaderData } from 'react-router-dom'
import {
  type AuthMethodsResponse,
  type AuthUser,
  errorMessage,
  getAuthMethods,
  linkGoogle,
  passwordAction,
  unlinkGoogle
} from '../features/auth/auth.api'
import { useEffect, useState } from 'react'
import { PasswordField } from '../features/auth/PasswordField'
import { PasswordChecklist } from '../features/auth/PasswordChecklist'
import { GoogleSignInButton } from '../features/auth/GoogleSignInButton'
import { PageHeading } from '../components/layout/PageHeading'
import { useModalFocusTrap } from '../hooks/useModalFocusTrap'

export function PasswordPage() {
  const user = useLoaderData() as AuthUser
  const isCustomer = user.role === 'CUSTOMER'
  const result = useActionData<typeof passwordAction>()
  const busy = useNavigation().state !== 'idle'
  const [password, setPassword] = useState('')
  const [authMethods, setAuthMethods] = useState<AuthMethodsResponse | null>(null)
  const [methodsLoading, setMethodsLoading] = useState(isCustomer)
  const [actionError, setActionError] = useState<string | null>(null)
  const [actionSuccess, setActionSuccess] = useState<string | null>(null)

  // Unlink modal state
  const [showUnlinkModal, setShowUnlinkModal] = useState(false)
  const [unlinkPassword, setUnlinkPassword] = useState('')
  const [unlinking, setUnlinking] = useState(false)

  // Link Google state
  const [linking, setLinking] = useState(false)
  const [linkPassword, setLinkPassword] = useState('')
  const [showLinkModal, setShowLinkModal] = useState(false)
  const modalOpen = showUnlinkModal || showLinkModal
  const closeOpenModal = () => {
    setShowUnlinkModal(false)
    setShowLinkModal(false)
    setUnlinkPassword('')
    setLinkPassword('')
  }
  const modalRef = useModalFocusTrap<HTMLDivElement>(modalOpen, closeOpenModal, unlinking || linking)

  async function loadMethods(signal?: AbortSignal) {
    try {
      const methods = await getAuthMethods(signal)
      setAuthMethods(methods)
    } catch (err) {
      setActionError(errorMessage(err))
    } finally {
      setMethodsLoading(false)
    }
  }

  useEffect(() => {
    if (!isCustomer) return
    let ignore = false
    const controller = new AbortController()

    getAuthMethods(controller.signal)
      .then((methods) => {
        if (!ignore) {
          setAuthMethods(methods)
          setMethodsLoading(false)
        }
      })
      .catch((err) => {
        if (!ignore) {
          setActionError(errorMessage(err))
          setMethodsLoading(false)
        }
      })

    return () => {
      ignore = true
      controller.abort()
    }
  }, [isCustomer])

  async function handleUnlinkSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!unlinkPassword) return
    try {
      setUnlinking(true)
      setActionError(null)
      await unlinkGoogle(unlinkPassword)
      // Unlink revokes all sessions for security
      window.location.assign('/login?unlinked=1')
    } catch (err) {
      setUnlinking(false)
      setActionError(errorMessage(err))
    }
  }

  return (
    <section className="content-page security-page">
      <PageHeading title="Bảo mật tài khoản" description="Quản lý phương thức đăng nhập và giữ tài khoản an toàn." />

      {actionError && <p role="alert" className="auth-notice error">{actionError}</p>}
      {actionSuccess && <p role="status" className="auth-notice">{actionSuccess}</p>}

      <div className="security-layout">
        <aside className="security-note">
          <span className="security-symbol" aria-hidden="true">✓</span>
          <h2>Bảo vệ tài khoản</h2>
          <p>Sử dụng mật khẩu mạnh hoặc liên kết tài khoản Google để đăng nhập an toàn, tiện lợi.</p>
          <p>Khi đổi mật khẩu hoặc hủy liên kết Google, tất cả phiên đăng nhập sẽ được thu hồi để bảo vệ an toàn cho bạn.</p>
        </aside>

        <div className="security-main">
          {/* Section: Authentication Methods */}
          {isCustomer && <div className="auth-card" style={{ marginBottom: 24 }}>
            <h2>Phương thức đăng nhập</h2>
            <p className="court-form-intro">Các cách bạn có thể sử dụng để đăng nhập vào tài khoản của mình.</p>

            {methodsLoading ? (
              <p>Đang tải thông tin phương thức đăng nhập…</p>
            ) : authMethods ? (
              <div className="auth-methods-list">
                {/* Password Method */}
                <div className="auth-method-card">
                  <div className="auth-method-info">
                    <div className="auth-method-title">
                      Mật khẩu
                      <span className={`auth-badge ${authMethods.hasPassword ? 'active' : 'inactive'}`}>
                        {authMethods.hasPassword ? 'Đã cài đặt' : 'Chưa cài đặt'}
                      </span>
                    </div>
                    <div className="auth-method-status">
                      {authMethods.hasPassword
                        ? 'Bạn có thể đăng nhập bằng email và mật khẩu của mình.'
                        : 'Tài khoản chưa có mật khẩu (đang đăng nhập qua Google).'}
                    </div>
                  </div>
                </div>

                {/* Google Method */}
                <div className="auth-method-card">
                  <div className="auth-method-info">
                    <div className="auth-method-title">
                      Tài khoản Google
                      <span className={`auth-badge ${authMethods.googleLinked ? 'active' : 'inactive'}`}>
                        {authMethods.googleLinked ? 'Đã liên kết' : 'Chưa liên kết'}
                      </span>
                    </div>
                    <div className="auth-method-status">
                      {authMethods.googleLinked
                        ? `Đã liên kết với Google (${authMethods.googleEmail || 'Google Account'}).`
                        : 'Liên kết Google để đăng nhập nhanh chỉ với một chạm.'}
                    </div>
                  </div>
                  <div>
                    {authMethods.googleLinked ? (
                      <button
                        type="button"
                        className="button-outline button-danger-outline"
                        disabled={!authMethods.hasPassword}
                        title={!authMethods.hasPassword ? 'Không thể hủy liên kết phương thức đăng nhập duy nhất' : undefined}
                        onClick={() => setShowUnlinkModal(true)}
                      >
                        Hủy liên kết
                      </button>
                    ) : (
                      <button
                        type="button"
                        className="button-outline"
                        onClick={() => setShowLinkModal(true)}
                      >
                        Liên kết Google
                      </button>
                    )}
                  </div>
                </div>
              </div>
            ) : null}
          </div>

          }
          {/* Section: Change Password (only if user has password) */}
          {(!isCustomer || authMethods?.hasPassword) ? (
            <Form method="post" className="auth-card">
              <h2>Đổi mật khẩu</h2>
              {result?.error && <p role="alert" className="auth-notice error">{result.error}</p>}
              <PasswordField name="currentPassword" label="Mật khẩu hiện tại" autoComplete="current-password" />
              <PasswordField name="newPassword" label="Mật khẩu mới" value={password} onChange={setPassword} describedBy="new-password-guide" />
              <PasswordChecklist password={password} id="new-password-guide" />
              <button type="submit" className="button-primary" disabled={busy}>
                {busy ? 'Đang lưu…' : 'Đổi mật khẩu'}
              </button>
            </Form>
          ) : authMethods && !authMethods.hasPassword ? (
            <div className="auth-card">
              <h2>Mật khẩu tài khoản</h2>
              <p className="auth-notice">
                Tài khoản của bạn hiện tại đăng nhập thông qua Google. Tính năng tạo mật khẩu độc lập sẽ sớm được ra mắt trong các bản cập nhật tới.
              </p>
            </div>
          ) : null}
        </div>
      </div>

      {/* Unlink Google Confirmation Modal */}
      {showUnlinkModal && (
        <div ref={modalRef} tabIndex={-1} className="unlink-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="unlink-title">
          <div className="unlink-modal">
            <h3 id="unlink-title" style={{ marginTop: 0 }}>Hủy liên kết Google</h3>
            <p style={{ fontSize: 14, color: '#62738a', lineHeight: 1.6 }}>
              Để bảo mật tài khoản, vui lòng nhập mật khẩu hiện tại để xác nhận hủy liên kết Google. Sau khi hủy, tất cả phiên đăng nhập sẽ được thu hồi.
            </p>
            <form onSubmit={handleUnlinkSubmit}>
              <PasswordField
                name="unlinkPassword"
                label="Mật khẩu hiện tại"
                value={unlinkPassword}
                onChange={setUnlinkPassword}
              />
              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 12, marginTop: 20 }}>
                <button
                  type="button"
                  className="button-outline"
                  onClick={() => { setShowUnlinkModal(false); setUnlinkPassword(''); }}
                  disabled={unlinking}
                >
                  Hủy bỏ
                </button>
                <button
                  type="submit"
                  className="button-primary"
                  style={{ background: '#dc2626' }}
                  disabled={unlinking || !unlinkPassword}
                >
                  {unlinking ? 'Đang xử lý…' : 'Xác nhận hủy liên kết'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Link Google Modal */}
      {showLinkModal && (
        <div ref={modalRef} tabIndex={-1} className="unlink-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="link-title">
          <div className="unlink-modal">
            <h3 id="link-title" style={{ marginTop: 0 }}>Liên kết tài khoản Google</h3>
            <p style={{ fontSize: 14, color: '#62738a', lineHeight: 1.6 }}>
              Tài khoản Google cần có địa chỉ email trùng khớp với email tài khoản của bạn.
            </p>
            {authMethods?.hasPassword && (
              <div style={{ marginBottom: 16 }}>
                <PasswordField
                  name="linkPassword"
                  label="Xác nhận mật khẩu hiện tại"
                  value={linkPassword}
                  onChange={setLinkPassword}
                />
              </div>
            )}
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12, marginTop: 16 }}>
              <GoogleSignInButton mode="link" onError={setActionError} onBusyChange={setLinking}
                onCredential={async (credential, challengeId, signal) => {
                  setActionError(null)
                  await linkGoogle(credential, challengeId, linkPassword || undefined, signal)
                  if (signal.aborted) return
                  setActionSuccess('Đã liên kết tài khoản Google thành công.')
                  setShowLinkModal(false)
                  setLinkPassword('')
                  setLinking(false)
                  await loadMethods()
                }} />
              <button
                type="button"
                className="button-outline"
                style={{ marginTop: 8 }}
                disabled={linking}
                onClick={() => { setShowLinkModal(false); setLinkPassword(''); }}
              >
                Đóng
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
