import { useState } from 'react'

type Props = {
  name: string; label: string; autoComplete?: string; value?: string;
  onChange?: (value: string) => void; describedBy?: string; invalid?: boolean;
}

export function PasswordField({ name, label, autoComplete = 'new-password', value, onChange, describedBy, invalid }: Props) {
  const [visible, setVisible] = useState(false)
  return <div className="form-field">
    <label htmlFor={name}>{label}</label>
    <div className="password-control">
      <input id={name} name={name} type={visible ? 'text' : 'password'} autoComplete={autoComplete} maxLength={128} required
        value={value} onChange={onChange ? event => onChange(event.target.value) : undefined}
        aria-describedby={describedBy} aria-invalid={invalid || undefined} placeholder={name === 'confirmPassword' ? 'Nhập lại mật khẩu vừa tạo' : autoComplete === 'current-password' ? 'Nhập mật khẩu của bạn' : 'Tạo mật khẩu an toàn'} />
      <button type="button" aria-label={`${visible ? 'Ẩn' : 'Hiện'} ${label.toLowerCase()}`} aria-pressed={visible} onClick={() => setVisible(!visible)}>
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>{visible && <path d="m3 3 18 18"/>}</svg>
      </button>
    </div>
  </div>
}
