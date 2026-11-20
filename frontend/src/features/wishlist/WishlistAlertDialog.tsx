import { useEffect, useId, useRef, useState } from 'react'
import { ApiError } from '../../lib/api/client'
import { formatPrice } from '../catalog/catalog.api'
import type { WishlistAlertSettings, WishlistAlertSummary, WishlistAvailability } from './wishlist.api'
import { useWishlist } from './useWishlist'
import './wishlist-alert-dialog.css'

type Props = {
  productId: string
  productTitle: string
  availability?: WishlistAvailability
  initialSummary?: WishlistAlertSummary
  className?: string
}

export function WishlistAlertDialog({ productId, productTitle, availability, initialSummary, className = '' }: Props) {
  const { getAlertSummary } = useWishlist()
  const summary = getAlertSummary(productId) ?? initialSummary
  const [open, setOpen] = useState(false)
  const enabled = Boolean(summary?.backInStockEnabled || summary?.priceDropEnabled)

  return <>
    <button
      type="button"
      className={`wishlist-alert-trigger${enabled ? ' is-active' : ''} ${className}`.trim()}
      onClick={() => setOpen(true)}
      aria-haspopup="dialog"
      data-testid={`wishlist-alert-trigger-${productId}`}
    >
      <span aria-hidden="true">{enabled ? '🔔' : '♧'}</span>
      {enabled ? 'Đang theo dõi' : 'Cài cảnh báo'}
    </button>
    {open && <AlertSettingsDialog
      productId={productId}
      productTitle={productTitle}
      availability={availability}
      summary={summary}
      onClose={() => setOpen(false)}
    />}
  </>
}

function AlertSettingsDialog({
  productId,
  productTitle,
  availability,
  summary,
  onClose,
}: {
  productId: string
  productTitle: string
  availability?: WishlistAvailability
  summary?: WishlistAlertSummary
  onClose: () => void
}) {
  const { emailVerified, loadAlertSettings, saveAlertSettings } = useWishlist()
  const dialogRef = useRef<HTMLDialogElement>(null)
  const closeButtonRef = useRef<HTMLButtonElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  const [settings, setSettings] = useState<WishlistAlertSettings | null>(null)
  const [draft, setDraft] = useState(() => ({
    backInStockEnabled: summary?.backInStockEnabled ?? false,
    priceDropEnabled: summary?.priceDropEnabled ?? false,
    emailEnabled: summary?.emailEnabled ?? false,
  }))
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const dialog = dialogRef.current
    dialog?.showModal()
    closeButtonRef.current?.focus()
    const controller = new AbortController()
    void loadAlertSettings(productId, controller.signal)
      .then(current => {
        if (controller.signal.aborted) return
        setSettings(current)
        setDraft({
          backInStockEnabled: current.backInStockEnabled,
          priceDropEnabled: current.priceDropEnabled,
          emailEnabled: current.emailEnabled,
        })
      })
      .catch(err => {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setMessage({ kind: 'error', text: 'Chưa tải được cấu hình cảnh báo. Vui lòng thử lại.' })
        }
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => {
      controller.abort()
      dialog?.close()
      if (previous?.isConnected) previous.focus()
    }
  }, [loadAlertSettings, productId])

  const currentAvailability = settings?.currentAvailability ?? availability
  const unavailable = currentAvailability === 'PRODUCT_UNAVAILABLE'
  const hasType = draft.backInStockEnabled || draft.priceDropEnabled

  async function save() {
    if (!settings || saving) return
    setSaving(true)
    setMessage(null)
    try {
      const updated = await saveAlertSettings(productId, {
        ...draft,
        emailEnabled: hasType && draft.emailEnabled,
        expectedRevision: settings.revision,
      })
      setSettings(updated)
      setDraft({
        backInStockEnabled: updated.backInStockEnabled,
        priceDropEnabled: updated.priceDropEnabled,
        emailEnabled: updated.emailEnabled,
      })
      setMessage({ kind: 'success', text: 'Đã lưu lựa chọn cảnh báo.' })
    } catch (err) {
      if (err instanceof ApiError && err.code === 'STALE_WISHLIST_ALERT_SETTINGS') {
        const latest = await loadAlertSettings(productId).catch(() => null)
        if (latest) {
          setSettings(latest)
          setDraft({
            backInStockEnabled: latest.backInStockEnabled,
            priceDropEnabled: latest.priceDropEnabled,
            emailEnabled: latest.emailEnabled,
          })
        }
        setMessage({ kind: 'error', text: 'Cấu hình đã thay đổi ở nơi khác. Dữ liệu mới nhất đã được tải lại.' })
      } else if (err instanceof ApiError && err.code === 'EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL') {
        setDraft(previous => ({ ...previous, emailEnabled: false }))
        setMessage({ kind: 'error', text: 'Bạn cần xác minh email trước khi nhận cảnh báo qua email.' })
      } else if (err instanceof ApiError && err.code === 'WISHLIST_ALERT_PRODUCT_UNAVAILABLE') {
        setMessage({ kind: 'error', text: 'Sản phẩm không còn mở bán. Bạn chỉ có thể tắt các cảnh báo hiện có.' })
      } else {
        setMessage({ kind: 'error', text: 'Chưa thể lưu cấu hình cảnh báo. Vui lòng thử lại.' })
      }
    } finally {
      setSaving(false)
    }
  }

  return <dialog
    ref={dialogRef}
    className="wishlist-alert-dialog"
    aria-labelledby={titleId}
    aria-describedby={descriptionId}
    onCancel={event => { event.preventDefault(); if (!saving) onClose() }}
    onKeyDown={event => {
      if (event.key === 'Escape' && !saving) { event.preventDefault(); onClose(); return }
      trapFocus(event)
    }}
  >
    <div className="wishlist-alert-dialog-head">
      <div>
        <span className="wishlist-alert-kicker">THEO DÕI SẢN PHẨM</span>
        <h2 id={titleId}>Nhận thông báo</h2>
      </div>
      <button ref={closeButtonRef} type="button" className="wishlist-alert-close" onClick={onClose} disabled={saving} aria-label="Đóng cài đặt cảnh báo">×</button>
    </div>
    <p id={descriptionId} className="wishlist-alert-product">{productTitle}</p>

    {loading ? <p role="status" className="wishlist-alert-loading">Đang tải cấu hình…</p> : <>
      {unavailable && <p className="wishlist-alert-unavailable" role="note">Sản phẩm đã ngừng mở bán. Bạn có thể tắt cảnh báo hiện có.</p>}
      {settings?.currentMinPrice !== null && settings?.currentMinPrice !== undefined && (
        <p className="wishlist-alert-price">Giá thường hiện tại: <strong>{formatPrice(settings.currentMinPrice)}</strong></p>
      )}
      <fieldset disabled={saving}>
        <legend>Bạn muốn được báo khi nào?</legend>
        <label>
          <input type="checkbox" checked={draft.backInStockEnabled}
            disabled={saving || (unavailable && !draft.backInStockEnabled)}
            onChange={event => setDraft(previous => ({ ...previous, backInStockEnabled: event.target.checked, emailEnabled: event.target.checked || previous.priceDropEnabled ? previous.emailEnabled : false }))} />
          <span><strong>Có hàng trở lại</strong><small>Khi một phiên bản có thể đặt mua lại.</small></span>
        </label>
        <label>
          <input type="checkbox" checked={draft.priceDropEnabled}
            disabled={saving || (unavailable && !draft.priceDropEnabled)}
            onChange={event => setDraft(previous => ({ ...previous, priceDropEnabled: event.target.checked, emailEnabled: event.target.checked || previous.backInStockEnabled ? previous.emailEnabled : false }))} />
          <span><strong>Giá thường giảm</strong><small>Không tính Flash Sale hoặc voucher ngắn hạn.</small></span>
        </label>
      </fieldset>

      <fieldset disabled={saving || !hasType}>
        <legend>Kênh nhận tin</legend>
        <label className="wishlist-alert-email">
          <input type="checkbox" checked={hasType && draft.emailEnabled}
            disabled={saving || !hasType || !emailVerified}
            onChange={event => setDraft(previous => ({ ...previous, emailEnabled: event.target.checked }))} />
          <span><strong>Gửi thêm qua email</strong><small>{emailVerified ? 'Email tài khoản đã được xác minh.' : 'Bạn cần xác minh email trước khi bật kênh này.'}</small></span>
        </label>
        <p className="wishlist-alert-channel-note">Thông báo trong ứng dụng luôn được bật cho loại cảnh báo bạn chọn.</p>
      </fieldset>
    </>}

    {message && <p className={`wishlist-alert-message ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}>{message.text}</p>}
    <div className="wishlist-alert-dialog-actions">
      <button type="button" className="button-secondary" onClick={onClose} disabled={saving}>Đóng</button>
      <button type="button" className="button-primary" onClick={() => void save()} disabled={loading || saving || !settings}>
        {saving ? 'Đang lưu…' : 'Lưu cảnh báo'}
      </button>
    </div>
  </dialog>
}

function trapFocus(event: React.KeyboardEvent<HTMLDialogElement>) {
  if (event.key !== 'Tab') return
  const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), a[href]'))
  const first = controls[0]
  const last = controls[controls.length - 1]
  if (!first || !last) { event.preventDefault(); return }
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
}
