import { Form, useActionData, useNavigation } from 'react-router-dom'
import { passwordAction } from '../features/auth/auth.api'
import { useState } from 'react'
import { PasswordField } from '../features/auth/PasswordField'
import { PasswordChecklist } from '../features/auth/PasswordChecklist'
import { PageHeading } from '../components/layout/PageHeading'

export function PasswordPage() {
  const result = useActionData<typeof passwordAction>()
  const busy = useNavigation().state !== 'idle'
  const [password, setPassword] = useState('')
  return <section className="content-page security-page">
    <PageHeading title="Bảo mật tài khoản" description="Giữ tài khoản an toàn để an tâm mua sắm." />
    <div className="security-layout"><aside className="security-note"><span className="security-symbol" aria-hidden="true">✓</span><h2>Mật khẩu tốt, an tâm hơn</h2><p>Dùng mật khẩu riêng cho tài khoản này. Kết hợp chữ hoa, chữ thường, số và ký tự đặc biệt.</p><p>Sau khi đổi mật khẩu, bạn sẽ cần đăng nhập lại trên mọi thiết bị.</p></aside>
    <Form method="post" className="auth-card">
    <h2>Đổi mật khẩu</h2>
    {result?.error && <p role="alert">{result.error}</p>}
    <PasswordField name="currentPassword" label="Mật khẩu hiện tại" autoComplete="current-password" />
    <PasswordField name="newPassword" label="Mật khẩu mới" value={password} onChange={setPassword} describedBy="new-password-guide" />
    <PasswordChecklist password={password} id="new-password-guide" />
    <button type="submit" className="button-primary" disabled={busy}>{busy ? 'Đang lưu…' : 'Đổi mật khẩu'}</button>
  </Form></div></section>
}

