export const passwordRules = [
  { label: '12–128 ký tự', check: (value: string) => value.length >= 12 && value.length <= 128 },
  { label: 'Chữ hoa (A–Z)', check: (value: string) => /[A-Z]/.test(value) },
  { label: 'Chữ thường (a–z)', check: (value: string) => /[a-z]/.test(value) },
  { label: 'Số (0–9)', check: (value: string) => /[0-9]/.test(value) },
  { label: 'Ký tự đặc biệt (!, @, #…)', check: (value: string) => /[^\p{L}\p{N}\s]/u.test(value) },
] as const

export function passwordError(password: string): string | undefined {
  return passwordRules.every(rule => rule.check(password)) ? undefined
    : 'Mật khẩu cần 12–128 ký tự, có chữ hoa, chữ thường, số và ký tự đặc biệt (ví dụ @).'
}
