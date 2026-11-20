import { useEffect, useId, useRef } from 'react'
import type { ReactNode } from 'react'

type Props = {
  title: string
  description: string
  confirmLabel: string
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
  children?: ReactNode
}

export function ConfirmDialog({ title, description, confirmLabel, busy = false, onConfirm, onCancel, children }: Props) {
  const dialog = useRef<HTMLDialogElement>(null)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const element = dialog.current
    element?.showModal()
    cancelButton.current?.focus()
    return () => {
      element?.close()
      if (previous?.isConnected) previous.focus()
    }
  }, [])
  return <dialog ref={dialog} className="confirm-dialog" aria-labelledby={titleId} aria-describedby={descriptionId}
    onKeyDown={event => {
      if (event.key !== 'Tab') return
      const buttons = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), a[href]'))
      const first = buttons[0], last = buttons[buttons.length - 1]
      if (!first || !last) { event.preventDefault(); return }
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
    }}
    onCancel={event => { event.preventDefault(); if (!busy) onCancel() }}>
    <h2 id={titleId}>{title}</h2>
    <p id={descriptionId}>{description}</p>
    {children}
    <div className="confirm-dialog-actions">
      <button ref={cancelButton} type="button" className="button-secondary" disabled={busy} onClick={onCancel}>Quay lại</button>
      <button type="button" className="button-danger" disabled={busy} onClick={onConfirm}>{busy ? 'Đang xử lý…' : confirmLabel}</button>
    </div>
  </dialog>
}
