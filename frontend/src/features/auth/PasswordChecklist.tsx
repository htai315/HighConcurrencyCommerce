import { passwordRules } from './password-policy'

export function PasswordChecklist({ password, id }: { password: string; id: string }) {
  return <div className="password-guide" id={id}><p>Mật khẩu cần có</p><ul>{passwordRules.map(rule => {
    const satisfied = rule.check(password)
    return <li key={rule.label} className={satisfied ? 'satisfied' : ''}><span aria-label={satisfied ? 'Đạt' : 'Chưa đạt'}>{satisfied ? '✓' : '○'}</span>{rule.label}</li>
  })}</ul></div>
}
