import { useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import {
  createGoogleChallenge,
  errorMessage,
  finishSignIn,
  getAuthProviders,
  googleLogin,
  safeReturnTo
} from './auth.api'

declare global {
  interface Window {
    google?: {
      accounts: {
        id: {
          initialize: (config: {
            client_id: string
            callback: (response: { credential: string }) => void
            nonce?: string
            auto_select?: boolean
            cancel_on_tap_outside?: boolean
          }) => void
          renderButton: (
            parent: HTMLElement,
            options: {
              type?: 'standard' | 'icon'
              theme?: 'outline' | 'filled_blue' | 'filled_black'
              size?: 'large' | 'medium' | 'small'
              text?: 'signin_with' | 'signup_with' | 'continue_with' | 'signin'
              shape?: 'rectangular' | 'pill' | 'circle' | 'square'
              logo_alignment?: 'left' | 'center'
              width?: number
              locale?: string
            }
          ) => void
          prompt?: (notification: (notification: unknown) => void) => void
          cancel: () => void
        }
      }
    }
  }
}

let scriptLoadingPromise: Promise<void> | null = null

function loadGoogleScript(): Promise<void> {
  if (typeof window === 'undefined') return Promise.resolve()
  if (window.google?.accounts?.id) return Promise.resolve()
  if (scriptLoadingPromise) return scriptLoadingPromise

  scriptLoadingPromise = new Promise<void>((resolve, reject) => {
    const existing = document.querySelector('script[src="https://accounts.google.com/gsi/client"]')
    const script = existing instanceof HTMLScriptElement ? existing : document.createElement('script')
    script.src = 'https://accounts.google.com/gsi/client'
    script.async = true
    script.defer = true
    const timeout = window.setTimeout(fail, 15000)
    function fail() {
      window.clearTimeout(timeout)
      script.remove()
      reject(new Error('GOOGLE_SDK_LOAD_FAILED'))
    }
    script.onload = () => { window.clearTimeout(timeout); resolve() }
    script.onerror = fail
    if (!existing) document.head.appendChild(script)
  }).catch((error: unknown) => {
    scriptLoadingPromise = null
    throw error
  })

  return scriptLoadingPromise
}

type GoogleSignInButtonProps = {
  mode: 'login' | 'register' | 'link'
  onError?: (message: string) => void
  onCredential?: (credential: string, challengeId: string, signal: AbortSignal) => Promise<void>
  onBusyChange?: (busy: boolean) => void
}

export function GoogleSignInButton({ mode, onError, onCredential, onBusyChange }: GoogleSignInButtonProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const [loading, setLoading] = useState(true)
  const [enabled, setEnabled] = useState(false)
  const [clientId, setClientId] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [retryNeeded, setRetryNeeded] = useState(false)
  const callbacks = useRef({ onError, onCredential, onBusyChange })
  useEffect(() => { callbacks.current = { onError, onCredential, onBusyChange } }, [onError, onCredential, onBusyChange])
  const location = useLocation()

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    let used = false
    let expiryTimer: number | undefined

    async function init() {
      try {
        const providers = await getAuthProviders(controller.signal)
        if (!providers.google.enabled || !providers.google.clientId) {
          setEnabled(false)
          setClientId(null)
          setLoading(false)
          return
        }

        setEnabled(true)
        setClientId(providers.google.clientId)
        await loadGoogleScript()
        if (controller.signal.aborted || !containerRef.current || !window.google?.accounts?.id) return

        const challenge = await createGoogleChallenge(mode === 'link' ? 'LINK' : 'LOGIN', controller.signal)
        if (controller.signal.aborted || !containerRef.current) return
        // Expire locally before the server's five-minute deadline; no background
        // refresh loop that could replace another tab's browser challenge cookie.
        expiryTimer = window.setTimeout(() => {
          if (controller.signal.aborted || inFlight) return
          used = true
          setRetryNeeded(true)
          callbacks.current.onError?.('Phiên Google đã hết hạn. Hãy bấm thử lại để tạo phiên mới.')
        }, 4 * 60 * 1000)

        window.google.accounts.id.initialize({
          client_id: providers.google.clientId,
          callback: async (response) => {
            if (!response.credential || controller.signal.aborted || inFlight || used) return
            inFlight = true
            used = true
            window.clearTimeout(expiryTimer)
            try {
              setSubmitting(true)
              callbacks.current.onBusyChange?.(true)
              if (mode === 'link') {
                await callbacks.current.onCredential?.(response.credential, challenge.challengeId, controller.signal)
                return
              }
              const user = await googleLogin(response.credential, challenge.challengeId, controller.signal)
              if (controller.signal.aborted) return
              await finishSignIn(user, controller.signal)
              if (controller.signal.aborted) return
              const returnUrl = safeReturnTo(location.pathname + location.search)
              // Use assign to ensure complete fresh session state & header update
              window.location.assign(returnUrl)
            } catch (err) {
              if (!controller.signal.aborted) {
                setRetryNeeded(true)
                callbacks.current.onError?.(errorMessage(err))
              }
            } finally {
              inFlight = false
              if (!controller.signal.aborted) {
                setSubmitting(false)
                callbacks.current.onBusyChange?.(false)
              }
            }
          },
          nonce: challenge.nonce,
          auto_select: false,
          cancel_on_tap_outside: true,
        })

        containerRef.current.innerHTML = ''
        window.google.accounts.id.renderButton(containerRef.current, {
          theme: 'outline',
          size: 'large',
          type: 'standard',
          text: mode === 'register' ? 'signup_with' : 'signin_with',
          shape: 'rectangular',
          logo_alignment: 'left',
          width: Math.min(320, containerRef.current.parentElement?.clientWidth || 320),
          locale: 'vi'
        })
      } catch (err) {
        if (!controller.signal.aborted) {
          setRetryNeeded(true)
          callbacks.current.onError?.(errorMessage(err))
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      }
    }

    init()

    return () => {
      controller.abort()
      window.clearTimeout(expiryTimer)
      if (window.google?.accounts?.id) {
        window.google.accounts.id.cancel()
      }
    }
  }, [mode, location.pathname, location.search, attempt])

  if (!loading && !enabled && !retryNeeded) {
    return mode === 'link' ? <p role="status">Đăng nhập Google hiện chưa được bật.</p> : null
  }

  return (
    <div className="google-auth-wrapper" data-testid="google-auth-wrapper">
      {mode !== 'link' && <div className="auth-divider">
        <span>Hoặc</span>
      </div>}
      <div className="google-button-container" data-testid="google-button-container" data-client-id={clientId ?? undefined}>
        {loading && <div className="google-button-placeholder">Đang tải Google Sign-In…</div>}
        <div ref={containerRef} data-testid="google-gis-element" data-client-id={clientId ?? undefined} style={{ display: loading || submitting || retryNeeded ? 'none' : 'block' }} />
      </div>
      {retryNeeded && <button type="button" className="button-secondary" onClick={() => { setLoading(true); setRetryNeeded(false); setAttempt(current => current + 1) }}>Thử lại Google</button>}
      {submitting && <p className="auth-notice" aria-live="polite">Đang xác thực tài khoản Google…</p>}
      {mode !== 'link' && <p className="google-auth-disclaimer">
        Tiếp tục đồng nghĩa với việc bạn đồng ý với Điều khoản dịch vụ và Chính sách quyền riêng tư của HighCommerce.
      </p>}
    </div>
  )
}
