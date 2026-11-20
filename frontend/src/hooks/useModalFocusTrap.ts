import { useEffect, useRef } from 'react'

const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

export function useModalFocusTrap<T extends HTMLElement>(
  open: boolean,
  onClose: () => void,
  closeBlocked = false,
) {
  const containerRef = useRef<T>(null)
  const onCloseRef = useRef(onClose)
  const closeBlockedRef = useRef(closeBlocked)

  useEffect(() => {
    onCloseRef.current = onClose
    closeBlockedRef.current = closeBlocked
  }, [closeBlocked, onClose])

  useEffect(() => {
    if (!open) return

    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const container = containerRef.current
    if (!container) return
    const activeContainer = container

    const focusableElements = () => Array.from(activeContainer.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
    queueMicrotask(() => (focusableElements()[0] ?? activeContainer).focus())

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.preventDefault()
        if (!closeBlockedRef.current) onCloseRef.current()
        return
      }

      if (event.key !== 'Tab') return
      const focusable = focusableElements()
      if (focusable.length === 0) {
        event.preventDefault()
        activeContainer.focus()
        return
      }

      const first = focusable[0]!
      const last = focusable[focusable.length - 1]!
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      previouslyFocused?.focus()
    }
  }, [open])

  return containerRef
}
