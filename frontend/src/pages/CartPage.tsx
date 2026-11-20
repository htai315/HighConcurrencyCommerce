import { useEffect, useRef, useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { formatPrice } from '../features/catalog/catalog.api'
import { cartLoader, guestCart, removeCustomerCartItem, setCustomerCartItem, validateCustomerCart } from '../features/cart/cart.api'
import type { Cart, CartValidation } from '../features/cart/cart.api'
import { removeGuestItem, setGuestItemQuantity, takeCartMergeNotice } from '../features/cart/guest-cart'
import { ApiError } from '../lib/api/client'
import { clearCheckoutIntent, createCheckout } from '../features/checkout/checkout.api'
import type { CheckoutPaymentMethod, CheckoutResult } from '../features/checkout/checkout.api'
import { previewVoucher, VOUCHER_ERROR_MESSAGES, type VoucherPreview } from '../features/vouchers/voucher.api'
import { PageHeading } from '../components/layout/PageHeading'
import { StatusBadge } from '../components/layout/StatusBadge'
import { requestShippingQuotes } from '../features/shipping/shipping.api'
import type { ShippingQuote, ShippingQuoteResult } from '../features/shipping/shipping.api'
import { createPayOsAttempt, redirectToPayOs, rememberPayOsReturnOrder } from '../features/payments/payos.api'

type CartBannerMessage = { kind: 'success' | 'error'; text: string }

export function CartPage() {
  const loaded = useLoaderData<typeof cartLoader>()
  const [cart, setCart] = useState<Cart>(loaded.cart)
  const [validation, setValidation] = useState<CartValidation | null>(null)
  const [message, setMessage] = useState<CartBannerMessage | null>(() => {
    const notice = takeCartMergeNotice()
    return notice ? { kind: 'success', text: notice } : null
  })
  const [busySku, setBusySku] = useState<string | null>(null)
  const [addressId, setAddressId] = useState(() => loaded.addresses.find(address => address.isDefault)?.id ?? loaded.addresses[0]?.id ?? '')
  const [paymentMethod, setPaymentMethod] = useState<CheckoutPaymentMethod>('COD')
  const [checkingOut, setCheckingOut] = useState(false)
  const [payOsRedirecting, setPayOsRedirecting] = useState(false)
  const [checkoutResult, setCheckoutResult] = useState<CheckoutResult | null>(null)
  const [voucherCodeInput, setVoucherCodeInput] = useState('')
  const [appliedVoucher, setAppliedVoucher] = useState<VoucherPreview | null>(null)
  const [voucherLoading, setVoucherLoading] = useState(false)
  const [voucherError, setVoucherError] = useState<string | null>(null)
  const [shippingQuotes, setShippingQuotes] = useState<ShippingQuoteResult | null>(null)
  const [selectedShippingMethodId, setSelectedShippingMethodId] = useState('')
  const [shippingLoading, setShippingLoading] = useState(() => loaded.mode === 'customer' && Boolean(addressId) && cart.items.length > 0)
  const [shippingError, setShippingError] = useState<string | null>(null)
  const [shippingReload, setShippingReload] = useState(0)
  const requireShippingConfirmation = useRef(false)
  const activeShippingQuotes = (loaded.mode !== 'customer' || !addressId || cart.items.length === 0) ? null : shippingQuotes
  const selectedShipping = activeShippingQuotes?.quotes.find(quote => quote.shippingMethodId === selectedShippingMethodId) ?? null
  const merchandiseTotal = appliedVoucher ? appliedVoucher.totalAmount : cart.subtotal
  const checkoutTotal = merchandiseTotal + (selectedShipping?.shippingFee ?? 0)

  useEffect(() => {
    if (loaded.mode !== 'customer' || !addressId || cart.items.length === 0) {
      return
    }
    const controller = new AbortController()
    requestShippingQuotes(addressId, cart.items.map(item => ({
      productSkuId: item.productSkuId,
      quantity: item.quantity,
      flashSaleOfferId: item.flashSaleOfferId,
    })), controller.signal).then(result => {
      setShippingQuotes(result)
      if (requireShippingConfirmation.current) {
        setSelectedShippingMethodId('')
      } else {
        const initial = result.quotes.find(quote => quote.isDefault) ?? result.quotes[0]
        setSelectedShippingMethodId(initial?.shippingMethodId ?? '')
      }
      if (result.quotes.length === 0) setShippingError('Chưa có phương thức giao hàng phù hợp với địa chỉ này.')
    }).catch(error => {
      if (controller.signal.aborted) return
      setShippingQuotes(null)
      setSelectedShippingMethodId('')
      setShippingError(error instanceof ApiError && error.message ? error.message : 'Chưa thể tải phí vận chuyển. Vui lòng thử lại.')
    }).finally(() => {
      if (!controller.signal.aborted) setShippingLoading(false)
    })
    return () => controller.abort()
  }, [addressId, cart.items, cart.items.length, loaded.mode, shippingReload])

  async function applyVoucher() {
    const code = voucherCodeInput.trim()
    if (!code) return
    if (cart.items.length === 0) {
      setVoucherError('Giỏ hàng đang trống.')
      return
    }
    setVoucherLoading(true)
    setVoucherError(null)
    try {
      const preview = await previewVoucher(code)
      setAppliedVoucher(preview)
      clearCheckoutIntent()
    } catch (err: unknown) {
      setAppliedVoucher(null)
      clearCheckoutIntent()
      if (err instanceof ApiError && err.code && VOUCHER_ERROR_MESSAGES[err.code]) {
        setVoucherError(VOUCHER_ERROR_MESSAGES[err.code]!)
      } else if (err instanceof ApiError && err.message) {
        setVoucherError(err.message)
      } else {
        setVoucherError('Không thể áp dụng mã giảm giá. Vui lòng thử lại.')
      }
    } finally {
      setVoucherLoading(false)
    }
  }

  function removeVoucher() {
    setAppliedVoucher(null)
    setVoucherCodeInput('')
    setVoucherError(null)
    clearCheckoutIntent()
  }

  async function changeQuantity(skuId: string, quantity: number) {
    if (checkingOut || busySku !== null) return
    setBusySku(skuId); setValidation(null); setMessage(null)
    requireShippingConfirmation.current = false
    setSelectedShippingMethodId('')
    setShippingLoading(true)
    setShippingError(null)
    clearCheckoutIntent()
    if (appliedVoucher) {
      setAppliedVoucher(null)
      clearCheckoutIntent()
    }
    try {
      setCart(loaded.mode === 'customer'
        ? await setCustomerCartItem(skuId, quantity, cart.items.find(item => item.productSkuId === skuId)?.flashSaleOfferId)
        : guestCart(setGuestItemQuantity(skuId, quantity)))
    } catch { setMessage({ kind: 'error', text: 'Chưa thể cập nhật số lượng. Vui lòng thử lại.' }) }
    finally { setBusySku(null) }
  }

  async function remove(skuId: string) {
    if (checkingOut || busySku !== null) return
    setBusySku(skuId); setValidation(null); setMessage(null)
    requireShippingConfirmation.current = false
    setSelectedShippingMethodId('')
    setShippingLoading(true)
    setShippingError(null)
    clearCheckoutIntent()
    if (appliedVoucher) {
      setAppliedVoucher(null)
      clearCheckoutIntent()
    }
    try {
      setCart(loaded.mode === 'customer'
        ? await removeCustomerCartItem(skuId)
        : guestCart(removeGuestItem(skuId)))
    } catch { setMessage({ kind: 'error', text: 'Chưa thể xóa sản phẩm. Vui lòng thử lại.' }) }
    finally { setBusySku(null) }
  }

  async function validate() {
    setMessage(null)
    requireShippingConfirmation.current = false
    setSelectedShippingMethodId('')
    setShippingLoading(true)
    setShippingError(null)
    clearCheckoutIntent()
    if (appliedVoucher) {
      setAppliedVoucher(null)
      clearCheckoutIntent()
    }
    try {
      const result = await validateCustomerCart()
      setValidation(result); setCart(result.cart)
      if (result.isValid) setMessage({ kind: 'success', text: 'Giỏ hàng hợp lệ ở thời điểm kiểm tra. Tồn kho vẫn chưa được giữ.' })
    } catch { setMessage({ kind: 'error', text: 'Chưa thể kiểm tra giỏ hàng. Vui lòng thử lại.' }) }
  }

  async function checkout() {
    if (!addressId || !selectedShipping || cart.items.length === 0 || checkingOut || busySku !== null) return
    setCheckingOut(true); setMessage(null); setValidation(null)
    try {
      const result = await createCheckout(addressId, paymentMethod,
        cart.items.map(item => ({ productSkuId: item.productSkuId, quantity: item.quantity, flashSaleOfferId: item.flashSaleOfferId })),
        { shippingMethodId: selectedShipping.shippingMethodId, methodRevision: selectedShipping.methodRevision, shippingFee: selectedShipping.shippingFee },
        appliedVoucher?.code)
      setCheckoutResult(result)
      window.dispatchEvent(new Event('hcc:cart-changed'))
      setCart({ id: cart.id, items: [], totalQuantity: 0, subtotal: 0, canCheckout: false, updatedAt: null })
      setShippingQuotes(null)
      setSelectedShippingMethodId('')
      setAppliedVoucher(null)
      setVoucherCodeInput('')
      setMessage({ kind: 'success', text: `Đã tạo đơn ${result.orderNumber}. Tồn kho đã được giữ nguyên khối.` })
      if (result.paymentMethod === 'PAYOS') {
        setPayOsRedirecting(true)
        try {
          const attempt = await createPayOsAttempt(result.orderId)
          if (!attempt.checkoutUrl) throw new Error('PayOS checkout URL missing')
          rememberPayOsReturnOrder(result.orderId)
          redirectToPayOs(attempt.checkoutUrl)
        } catch {
          setMessage({ kind: 'error', text: `Đơn ${result.orderNumber} đã được tạo an toàn nhưng chưa mở được PayOS. Bạn có thể thử lại bên dưới hoặc trong trang Đơn hàng.` })
        } finally { setPayOsRedirecting(false) }
      }
    } catch (error) {
      if (error instanceof ApiError && error.code === 'SHIPPING_QUOTE_CHANGED') {
        clearCheckoutIntent()
        requireShippingConfirmation.current = true
        setSelectedShippingMethodId('')
        setShippingLoading(true)
        setShippingReload(value => value + 1)
        setMessage({ kind: 'error', text: 'Phí vận chuyển vừa thay đổi. Chúng tôi đã tải báo giá mới; vui lòng chọn lại phương thức trước khi đặt hàng.' })
      } else if (error instanceof ApiError && ['OUT_OF_STOCK', 'SKU_NOT_AVAILABLE', 'ADDRESS_NOT_FOUND', 'SHIPPING_METHOD_NOT_ACTIVE'].includes(error.code ?? '')) {
        clearCheckoutIntent()
        const text = error.code === 'OUT_OF_STOCK'
          ? (error.message || 'Một sản phẩm vừa hết hàng. Vui lòng cập nhật giỏ và thử lại.')
          : (error.message || 'Thông tin checkout không còn hợp lệ. Vui lòng kiểm tra lại.')
        setMessage({ kind: 'error', text })
      } else if (error instanceof ApiError && error.code === 'IDEMPOTENCY_KEY_REUSED') {
        clearCheckoutIntent()
        setMessage({ kind: 'error', text: 'Nội dung checkout đã thay đổi. Vui lòng gửi lại thao tác mới.' })
      } else if (error instanceof ApiError && error.code && [
        'VOUCHER_NOT_FOUND', 'VOUCHER_NOT_ACTIVE', 'VOUCHER_NOT_STARTED', 'VOUCHER_EXPIRED',
        'VOUCHER_MINIMUM_NOT_MET', 'VOUCHER_USAGE_LIMIT_REACHED', 'VOUCHER_CUSTOMER_LIMIT_REACHED',
        'VOUCHER_BUDGET_EXHAUSTED', 'VOUCHER_DISCOUNT_INVALID'
      ].includes(error.code)) {
        clearCheckoutIntent()
        setAppliedVoucher(null)
        setMessage({ kind: 'error', text: VOUCHER_ERROR_MESSAGES[error.code] || error.message || 'Mã giảm giá không hợp lệ khi đặt hàng.' })
      } else if (error instanceof ApiError && ['COD_PENDING_LIMIT_EXCEEDED', 'FLASH_SALE_QUOTA_EXCEEDED', 'FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED', 'FLASH_SALE_CAMPAIGN_NOT_ACTIVE', 'FLASH_SALE_INVALID_OFFER', 'FLASH_SALE_NOT_FOUND', 'CUSTOMER_NOT_AVAILABLE', 'VALIDATION_ERROR'].includes(error.code ?? '')) {
        clearCheckoutIntent()
        setMessage({ kind: 'error', text: error.message || 'Không thể tạo đơn hàng vào lúc này.' })
      } else {
        setMessage({ kind: 'error', text: 'Chưa xác định được checkout đã hoàn tất hay chưa. Hãy bấm thử lại; hệ thống sẽ dùng cùng mã chống trùng đơn.' })
      }
    } finally { setCheckingOut(false) }
  }

  async function resumePayOs() {
    if (!checkoutResult || checkoutResult.paymentMethod !== 'PAYOS' || payOsRedirecting) return
    setPayOsRedirecting(true); setMessage(null)
    try {
      const attempt = await createPayOsAttempt(checkoutResult.orderId)
      if (!attempt.checkoutUrl) throw new Error('PayOS checkout URL missing')
      rememberPayOsReturnOrder(checkoutResult.orderId)
      redirectToPayOs(attempt.checkoutUrl)
    } catch {
      setMessage({ kind: 'error', text: 'Chưa thể mở PayOS. Đơn hàng và phần tồn đang giữ không bị tạo trùng.' })
    } finally { setPayOsRedirecting(false) }
  }

  return (
    <section className="content-page cart-page">
      <PageHeading title="Giỏ hàng" description={loaded.mode === 'guest' ? 'Lựa chọn của bạn được lưu trên trình duyệt. Đăng nhập để tiếp tục đặt hàng.' : 'Kiểm tra sản phẩm, chọn địa chỉ và hoàn tất đơn hàng của bạn.'} extra={<Link className="back-link" to="/">Tiếp tục mua sắm</Link>} />

      {message && (
        <p
          role={message.kind === 'error' ? 'alert' : 'status'}
          className={`alert ${message.kind}`}
        >
          {message.text}
        </p>
      )}

      {checkoutResult && (
        <article className="checkout-success" aria-label="Đơn hàng vừa tạo">
          <div>
            <p className="eyebrow" style={{ color: 'var(--success-ink)' }}>{checkoutResult.paymentMethod === 'PAYOS' ? 'Đã tạo đơn chờ thanh toán' : 'Đặt hàng thành công'}</p>
            <h2>{checkoutResult.orderNumber}</h2>
          </div>
          <div>
            <span>Trạng thái đơn</span>
            <StatusBadge value={checkoutResult.orderStatus} />
          </div>
          <div>
            <span>Thanh toán</span>
            <StatusBadge value={checkoutResult.paymentStatus} />
          </div>
          <div>
            <span>Thời hạn</span>
            <strong>{new Date(checkoutResult.deadlineAt).toLocaleString('vi-VN')}</strong>
          </div>
          {checkoutResult.voucherCode && (
            <div>
              <span>Mã giảm giá</span>
              <strong style={{ color: 'var(--success-ink)' }}>{checkoutResult.voucherCode} (-{formatPrice(checkoutResult.voucherDiscountAmount)})</strong>
            </div>
          )}
          <div>
            <span>Phí vận chuyển</span>
            <strong>{checkoutResult.shippingFee === 0 ? 'Miễn phí' : formatPrice(checkoutResult.shippingFee)}</strong>
          </div>
          {checkoutResult.shippingMethod && (
            <div>
              <span>Phương thức giao</span>
              <strong>{checkoutResult.shippingMethod.name} · {checkoutResult.shippingMethod.estimatedMinDays}–{checkoutResult.shippingMethod.estimatedMaxDays} ngày</strong>
            </div>
          )}
          <div>
            <span>Tổng thanh toán</span>
            <strong style={{ color: 'var(--brand)', fontSize: 16 }}>{formatPrice(checkoutResult.totalAmount)}</strong>
          </div>
          {checkoutResult.paymentMethod === 'PAYOS' && checkoutResult.paymentStatus === 'PENDING' && (
            <button type="button" className="button-primary" disabled={payOsRedirecting} onClick={() => void resumePayOs()}>
              {payOsRedirecting ? 'Đang mở PayOS…' : 'Thanh toán qua PayOS'}
            </button>
          )}
          <Link to="/orders" className="button-secondary">Xem đơn hàng</Link>
        </article>
      )}

      {cart.items.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon" aria-hidden="true">🛒</div>
          <h2>Giỏ hàng đang trống</h2>
          <p>Chọn vợt, trang phục hoặc phụ kiện phù hợp để bắt đầu.</p>
          <Link to="/" className="button-primary">Tiếp tục mua sắm</Link>
        </div>
      ) : (
        <div className="cart-layout">
          <div className="cart-list" aria-label="Sản phẩm trong giỏ">
            {cart.items.map(item => (
              <article className="cart-item" key={item.productSkuId}>
                <div className="cart-item-media">
                  {item.imageUrl ? (
                    <img src={item.imageUrl.startsWith('http') || item.imageUrl.startsWith('/') ? item.imageUrl : `/${item.imageUrl}`} alt="" />
                  ) : (
                    <span aria-hidden="true" style={{ fontSize: 32 }}>🏸</span>
                  )}
                </div>
                <div className="cart-item-copy">
                  <Link to={`/products/${item.productId}`} style={{ color: 'var(--ink)' }}>
                    <h2>{item.productTitle}</h2>
                  </Link>
                  <p>{item.optionLabel} · {item.skuCode}</p>
                  <strong style={{ color: 'var(--brand)', fontSize: 16 }}>{formatPrice(item.unitPrice)}</strong>
                  {item.availabilityCode === 'INSUFFICIENT_STOCK' && <p className="cart-warning">Tồn kho hiện không đủ.</p>}
                  {item.availabilityCode === 'SKU_NOT_AVAILABLE' && <p className="cart-warning">Sản phẩm không còn được bán.</p>}
                  {item.availabilityCode === 'PRODUCT_SUSPENDED' && <p className="cart-warning">Sản phẩm đang tạm ngừng bán. Bạn vẫn có thể xóa khỏi giỏ.</p>}
                  {item.availabilityCode === 'PRODUCT_ARCHIVED' && <p className="cart-warning">Sản phẩm không còn kinh doanh. Bạn vẫn có thể xóa khỏi giỏ.</p>}
                </div>
                <div className="cart-item-actions">
                  <div className="quantity-control-group">
                    <span className="quantity-label">Số lượng</span>
                    <div className="quantity-stepper">
                      <button
                        type="button"
                        className="stepper-btn"
                        aria-label={`Giảm số lượng ${item.productTitle}`}
                        disabled={checkingOut || busySku !== null || item.quantity <= 1}
                        onClick={() => changeQuantity(item.productSkuId, Math.max(1, item.quantity - 1))}
                      >
                        −
                      </button>
                      <input
                        aria-label={`Số lượng ${item.productTitle}`}
                        type="number"
                        min="1"
                        max="99"
                        value={item.quantity}
                        disabled={checkingOut || busySku !== null}
                        onChange={event => changeQuantity(item.productSkuId, Math.min(99, Math.max(1, Number(event.target.value) || 1)))}
                      />
                      <button
                        type="button"
                        className="stepper-btn"
                        aria-label={`Tăng số lượng ${item.productTitle}`}
                        disabled={checkingOut || busySku !== null || item.quantity >= 99}
                        onClick={() => changeQuantity(item.productSkuId, Math.min(99, item.quantity + 1))}
                      >
                        +
                      </button>
                    </div>
                  </div>
                  <strong style={{ fontSize: 16 }}>{formatPrice(item.lineTotal)}</strong>
                  <button
                    type="button"
                    className="text-button"
                    style={{ color: 'var(--danger-ink)' }}
                    disabled={checkingOut || busySku !== null}
                    onClick={() => remove(item.productSkuId)}
                  >
                    Xóa
                  </button>
                </div>
              </article>
            ))}
          </div>

          <aside className="cart-summary">
            <h2>Tóm tắt đơn hàng</h2>
            <div>
              <span style={{ color: 'var(--muted)' }}>Số lượng</span>
              <strong>{cart.totalQuantity} món</strong>
            </div>
            <div className="cart-total">
              <span>Tạm tính</span>
              <strong>{formatPrice(cart.subtotal)}</strong>
            </div>

            {appliedVoucher && (
              <div className="cart-discount" style={{ display: 'flex', justifyContent: 'space-between', color: 'var(--success-ink, #16a34a)', margin: '4px 0' }}>
                <span>Mã giảm ({appliedVoucher.code})</span>
                <strong>-{formatPrice(appliedVoucher.discountAmount)}</strong>
              </div>
            )}

            <div className="cart-shipping-total">
              <span>Phí vận chuyển</span>
              <strong>{selectedShipping ? (selectedShipping.shippingFee === 0 ? 'Miễn phí' : formatPrice(selectedShipping.shippingFee)) : 'Chọn phương thức'}</strong>
            </div>

            <div className="cart-final-total" style={{ display: 'flex', justifyContent: 'space-between', fontSize: 16, fontWeight: 700, margin: '8px 0', paddingTop: 8, borderTop: '1px solid var(--border-color, #e2e8f0)' }}>
              <span>Tổng thanh toán</span>
              <strong style={{ color: 'var(--brand)' }}>
                {formatPrice(checkoutTotal)}
              </strong>
            </div>

            {loaded.mode === 'customer' && (
              <div className="voucher-card" style={{ marginTop: 14, marginBottom: 14, padding: '12px', background: 'var(--surface-muted, #f8fafc)', border: '1px solid var(--border-color, #e2e8f0)', borderRadius: 8 }}>
                <label htmlFor="voucher-code-input" style={{ display: 'block', fontSize: 13, fontWeight: 700, marginBottom: 6, color: 'var(--ink)' }}>
                  Mã ưu đãi
                </label>
                {appliedVoucher ? (
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                      <span className="badge badge-success" style={{ fontWeight: 700 }}>{appliedVoucher.code}</span>
                      <span style={{ fontSize: 12, color: 'var(--success-ink, #16a34a)' }}>
                        -{formatPrice(appliedVoucher.discountAmount)}
                      </span>
                    </div>
                    <button
                      type="button"
                      className="text-button"
                      style={{ color: 'var(--danger-ink, #dc2626)', fontSize: 12 }}
                      onClick={removeVoucher}
                      disabled={checkingOut}
                    >
                      Bỏ mã
                    </button>
                  </div>
                ) : (
                  <div style={{ display: 'flex', gap: 8 }}>
                    <input
                      id="voucher-code-input"
                      type="text"
                      value={voucherCodeInput}
                      placeholder="Nhập mã ưu đãi"
                      onChange={e => { setVoucherCodeInput(e.target.value.toUpperCase()); setVoucherError(null) }}
                      disabled={checkingOut || voucherLoading}
                      style={{ flex: 1, padding: '6px 10px', fontSize: 13, textTransform: 'uppercase' }}
                    />
                    <button
                      type="button"
                      className="button-secondary"
                      style={{ padding: '6px 12px', fontSize: 13, whiteSpace: 'nowrap' }}
                      onClick={applyVoucher}
                      disabled={checkingOut || voucherLoading || !voucherCodeInput.trim()}
                    >
                      {voucherLoading ? 'Đang xét…' : 'Áp dụng'}
                    </button>
                  </div>
                )}
                {voucherError && (
                  <p role="alert" style={{ color: 'var(--danger-ink, #dc2626)', fontSize: 12, margin: '6px 0 0' }}>
                    {voucherError}
                  </p>
                )}
              </div>
            )}

            <p className="cart-note">
              Giá và tình trạng sản phẩm được kiểm tra lại khi bạn đặt hàng.
            </p>

            {loaded.mode === 'customer' ? (
              <>
                <button
                  type="button"
                  className="button-secondary"
                  style={{ width: '100%', marginTop: 12, marginBottom: 12 }}
                  onClick={validate}
                >
                  Kiểm tra giỏ hàng
                </button>

                {validation && !validation.isValid && (
                  <ul className="cart-issues" role="alert" style={{ background: '#fef2f2', border: '1px solid #fecaca', borderRadius: 8, padding: '12px 16px 12px 28px', color: '#991b1b', margin: '12px 0' }}>
                    {validation.issues.map((issue, index) => (
                      <li key={`${issue.productSkuId ?? 'cart'}-${index}`}>{issue.message}</li>
                    ))}
                  </ul>
                )}

                <div className="checkout-fields">
                  {loaded.addresses.length > 0 ? (
                    <div className="shipping-choice">
                      <label>
                        <span style={{ fontSize: 13, fontWeight: 700, color: 'var(--ink)' }}>Địa chỉ giao hàng</span>
                        <select value={addressId} onChange={event => { requireShippingConfirmation.current = false; setSelectedShippingMethodId(''); setShippingLoading(true); setShippingError(null); setAddressId(event.target.value); clearCheckoutIntent() }}>
                          {loaded.addresses.map(address => (
                            <option value={address.id} key={address.id}>
                              {address.recipientName} — {address.addressLine}, {address.province}{address.isDefault ? ' (Mặc định)' : ''}
                            </option>
                          ))}
                        </select>
                      </label>
                      {loaded.addresses.filter(address => address.id === addressId).map(address => <div className="shipping-preview" key={address.id}><strong>{address.recipientName}</strong><span>{address.phone}</span><p>{[address.addressLine, address.ward, address.province].filter(Boolean).join(', ')}</p><Link to="/account">Quản lý địa chỉ</Link></div>)}
                    </div>
                  ) : (
                    <p className="cart-warning">
                      Bạn cần <Link to="/account" style={{ textDecoration: 'underline' }}>thêm địa chỉ giao hàng</Link> trước khi đặt hàng.
                    </p>
                  )}

                  <fieldset className="shipping-method-fieldset">
                    <legend>Phương thức giao hàng</legend>
                    {shippingLoading ? (
                      <div className="shipping-method-skeleton" role="status">Đang tính phí và thời gian giao…</div>
                    ) : shippingError ? (
                      <div className="shipping-method-error">
                        <p role="alert">{shippingError}</p>
                        <button type="button" className="button-secondary" onClick={() => { requireShippingConfirmation.current = false; setShippingLoading(true); setShippingError(null); setShippingReload(value => value + 1) }}>Thử lại</button>
                      </div>
                    ) : activeShippingQuotes && activeShippingQuotes.quotes.length > 0 ? (
                      <div className="shipping-method-list">
                        {activeShippingQuotes.quotes.map((quote: ShippingQuote) => (
                          <label className={`shipping-method-card ${selectedShippingMethodId === quote.shippingMethodId ? 'is-selected' : ''}`} key={quote.shippingMethodId}>
                            <input
                              type="radio"
                              name="shippingMethod"
                              value={quote.shippingMethodId}
                              checked={selectedShippingMethodId === quote.shippingMethodId}
                              onChange={() => {
                                requireShippingConfirmation.current = false
                                setSelectedShippingMethodId(quote.shippingMethodId)
                                clearCheckoutIntent()
                              }}
                            />
                            <span className="shipping-method-copy">
                              <span className="shipping-method-title">
                                <strong>{quote.name}</strong>
                                {quote.isDefault && <small className="shipping-badge">Khuyên dùng</small>}
                                {quote.freeShippingApplied && <small className="shipping-badge free">Miễn phí</small>}
                              </span>
                              {quote.description && <small>{quote.description}</small>}
                              <small>Dự kiến {quote.estimatedMinDays}–{quote.estimatedMaxDays} ngày</small>
                            </span>
                            <strong className="shipping-method-fee">{quote.shippingFee === 0 ? 'Miễn phí' : formatPrice(quote.shippingFee)}</strong>
                          </label>
                        ))}
                      </div>
                    ) : (
                      <p className="cart-warning">Chưa có phương thức giao hàng phù hợp.</p>
                    )}
                  </fieldset>

                  <fieldset>
                    <legend>Phương thức thanh toán</legend>
                    <div className="payment-method-options">
                    <label className={`payment-method-card ${paymentMethod === 'COD' ? 'is-selected' : ''}`}>
                      <input type="radio" name="paymentMethod" value="COD" checked={paymentMethod === 'COD'} onChange={() => { setPaymentMethod('COD'); clearCheckoutIntent() }} />
                      <span><strong>Thanh toán khi nhận hàng</strong><small>COD · Trả tiền khi đơn được giao</small></span>
                    </label>
                    <label className={`payment-method-card ${paymentMethod === 'PAYOS' ? 'is-selected' : ''} ${!loaded.paymentProviders.payOs.enabled ? 'is-disabled' : ''}`}>
                      <input type="radio" name="paymentMethod" value="PAYOS" disabled={!loaded.paymentProviders.payOs.enabled} checked={paymentMethod === 'PAYOS'} onChange={() => { setPaymentMethod('PAYOS'); clearCheckoutIntent() }} />
                      <span><strong>Chuyển khoản nhanh PayOS</strong><small>{loaded.paymentProviders.payOs.enabled ? 'QR ngân hàng · Xác minh tự động' : 'Sắp mở lại sau nghiệm thu sandbox'}</small></span>
                      <span className="payment-method-mark">QR</span>
                    </label>
                    </div>
                  </fieldset>

                  <button
                    type="button"
                    className="button-primary checkout-button"
                    onClick={checkout}
                    disabled={!cart.canCheckout || !addressId || !selectedShipping || shippingLoading || checkingOut || busySku !== null}
                  >
                    {checkingOut || payOsRedirecting ? 'Đang xử lý…' : paymentMethod === 'PAYOS' ? 'Đặt hàng & mở PayOS' : 'Đặt hàng'}
                  </button>
                </div>
              </>
            ) : (
              <Link className="button-primary" to="/login?returnTo=%2Fcart" style={{ display: 'flex', marginTop: 16 }}>
                Đăng nhập để tiếp tục
              </Link>
            )}
          </aside>
        </div>
      )}
    </section>
  )
}
