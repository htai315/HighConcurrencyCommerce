import { useState, type InputHTMLAttributes } from 'react'

type Props = InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string }

export function AuthInput({ label, hint, id, name, onBlur, ...props }: Props) {
  const [error, setError] = useState('')
  const fieldId = id ?? name
  return <div className="form-field">
    <label htmlFor={fieldId}>{label}</label>
    <input {...props} id={fieldId} name={name} aria-invalid={error ? true : undefined}
      aria-describedby={error || hint ? `${fieldId}-help` : undefined}
      onBlur={event => { setError(event.target.validationMessage); onBlur?.(event) }}
      onInvalid={event => setError(event.currentTarget.validationMessage)} />
    {(error || hint) && <small id={`${fieldId}-help`} className={error ? 'auth-field-error' : 'auth-field-hint'}>{error || hint}</small>}
  </div>
}
