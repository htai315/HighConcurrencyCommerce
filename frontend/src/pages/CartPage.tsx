import { useState } from 'react'
import { Link, useLoaderData } from 'react-router-dom'
import { formatPrice } from '../features/catalog/catalog.api'
import { cartLoader, guestCart, removeCustomerCartItem, setCustomerCartItem, validateCustomerCart } from '../features/cart/cart.api'
import type { Cart, CartValidation } from '../features/cart/cart.api'
import { removeGuestItem, setGuestItemQuantity, takeCartMergeNotice } from '../features/cart/guest-cart'
import { ApiError } from '../lib/api/client'
import { clearCheckoutIntent, createCheckout } from '../features/checkout/checkout.api'
import type { CheckoutPaymentMethod, CheckoutResult } from '../features/checkout/checkout.api'
import { PageHeading } from '../components/layout/PageHeading'

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
  const [checkoutResult, setCheckoutResult] = useState<CheckoutResult | null>(null)

  async function changeQuantity(skuId: string, quantity: number) {
    setBusySku(skuId); setValidation(null); setMessage(null)
    try {
      setCart(loaded.mode === 'customer'
        ? await setCustomerCartItem(skuId, quantity, cart.items.find(item => item.productSkuId === skuId)?.flashSaleOfferId)
        : guestCart(setGuestItemQuantity(skuId, quantity)))
    } catch { setMessage({ kind: 'error', text: 'Chưa thể cập nhật số lượng. Vui lòng thử lại.' }) }
    finally { setBusySku(null) }
  }

  async function remove(skuId: string) {
    setBusySku(skuId); setValidation(null); setMessage(null)
    try {
      setCart(loaded.mode === 'customer'
        ? await removeCustomerCartItem(skuId)
        : guestCart(removeGuestItem(skuId)))
    } catch { setMessage({ kind: 'error', text: 'Chưa thể xóa sản phẩm. Vui lòng thử lại.' }) }
    finally { setBusySku(null) }
  }

  async function validate() {
    setMessage(null)
    try {
      const result = await validateCustomerCart()
      setValidation(result); setCart(result.cart)
      if (result.isValid) setMessage({ kind: 'success', text: 'Giỏ hàng hợp lệ ở thời điểm kiểm tra. Tồn kho vẫn chưa được giữ.' })
    } catch { setMessage({ kind: 'error', text: 'Chưa thể kiểm tra giỏ hàng. Vui lòng thử lại.' }) }
  }

  async function checkout() {
    if (!addressId || cart.items.length === 0) return
    setCheckingOut(true); setMessage(null); setValidation(null)
    try {
      const result = await createCheckout(addressId, paymentMethod,
        cart.items.map(item => ({ productSkuId: item.productSkuId, quantity: item.quantity, flashSaleOfferId: item.flashSaleOfferId })))
      setCheckoutResult(result)
      setCart({ id: cart.id, items: [], totalQuantity: 0, subtotal: 0, canCheckout: false, updatedAt: new Date().toISOString() })
      setMessage({ kind: 'success', text: `Đã tạo đơn ${result.orderNumber}. Tồn kho đã được giữ nguyên khối.` })
    } catch (error) {
      if (error instanceof ApiError && ['OUT_OF_STOCK', 'SKU_NOT_AVAILABLE', 'ADDRESS_NOT_FOUND'].includes(error.code ?? '')) {
        clearCheckoutIntent()
        const text = error.code === 'OUT_OF_STOCK'
          ? (error.message || 'Một sản phẩm vừa hết hàng. Vui lòng cập nhật giỏ và thử lại.')
          : (error.message || 'Thông tin checkout không còn hợp lệ. Vui lòng kiểm tra lại.')
        setMessage({ kind: 'error', text })
      } else if (error instanceof ApiError && error.code === 'IDEMPOTENCY_KEY_REUSED') {
        clearCheckoutIntent()
        setMessage({ kind: 'error', text: 'Nội dung checkout đã thay đổi. Vui lòng gửi lại thao tác mới.' })
      } else if (error instanceof ApiError && ['COD_PENDING_LIMIT_EXCEEDED', 'FLASH_SALE_QUOTA_EXCEEDED', 'FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED', 'FLASH_SALE_CAMPAIGN_NOT_ACTIVE', 'FLASH_SALE_INVALID_OFFER', 'FLASH_SALE_NOT_FOUND', 'CUSTOMER_NOT_AVAILABLE', 'VALIDATION_ERROR'].includes(error.code ?? '')) {
        clearCheckoutIntent()
        setMessage({ kind: 'error', text: error.message || 'Không thể tạo đơn hàng vào lúc này.' })
      } else {
        setMessage({ kind: 'error', text: 'Chưa xác định được checkout đã hoàn tất hay chưa. Hãy bấm thử lại; hệ thống sẽ dùng cùng mã chống trùng đơn.' })
      }
    } finally { setCheckingOut(false) }
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
            <p className="eyebrow" style={{ color: 'var(--success-ink)' }}>Checkout thành công</p>
            <h2>{checkoutResult.orderNumber}</h2>
          </div>
          <div>
            <span>Trạng thái đơn</span>
            <strong>{checkoutResult.orderStatus}</strong>
          </div>
          <div>
            <span>Thanh toán</span>
            <strong>{checkoutResult.paymentStatus}</strong>
          </div>
          <div>
            <span>Thời hạn</span>
            <strong>{new Date(checkoutResult.deadlineAt).toLocaleString('vi-VN')}</strong>
          </div>
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
                </div>
                <div className="cart-item-actions">
                  <label>
                    Số lượng
                    <input
                      aria-label={`Số lượng ${item.productTitle}`}
                      type="number"
                      min="1"
                      max="99"
                      value={item.quantity}
                      disabled={busySku === item.productSkuId}
                      onChange={event => changeQuantity(item.productSkuId, Math.min(99, Math.max(1, Number(event.target.value) || 1)))}
                    />
                  </label>
                  <strong style={{ fontSize: 16 }}>{formatPrice(item.lineTotal)}</strong>
                  <button
                    type="button"
                    className="text-button"
                    style={{ color: 'var(--danger-ink)' }}
                    disabled={busySku === item.productSkuId}
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
            <p className="cart-note">
              Giá và tồn kho sẽ được kiểm tra lại khi đặt hàng. Hàng hóa chỉ được đảm bảo giữ tồn sau khi bấm đặt hàng.
            </p>

            {loaded.mode === 'customer' ? (
              <>
                <button
                  type="button"
                  className="button-secondary"
                  style={{ width: '100%', marginTop: 12, marginBottom: 12 }}
                  onClick={validate}
                >
                  Kiểm tra trước checkout
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
                    <label>
                      <span style={{ fontSize: 13, fontWeight: 700, color: 'var(--ink)' }}>Địa chỉ giao hàng</span>
                      <select value={addressId} onChange={event => { setAddressId(event.target.value); clearCheckoutIntent() }}>
                        {loaded.addresses.map(address => (
                          <option value={address.id} key={address.id}>
                            {address.recipientName} — {address.addressLine}, {address.province}{address.isDefault ? ' (Mặc định)' : ''}
                          </option>
                        ))}
                      </select>
                    </label>
                  ) : (
                    <p className="cart-warning">
                      Bạn cần <Link to="/account" style={{ textDecoration: 'underline' }}>thêm địa chỉ giao hàng</Link> trước khi checkout.
                    </p>
                  )}

                  <fieldset>
                    <legend>Phương thức thanh toán</legend>
                    <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', fontWeight: 600 }}>
                      <input type="radio" name="paymentMethod" value="COD" checked={paymentMethod === 'COD'} onChange={() => { setPaymentMethod('COD'); clearCheckoutIntent() }} />
                      💵 COD (Thanh toán khi nhận)
                    </label>
                    <label style={{ display: 'flex', alignItems: 'center', gap: 8, opacity: 0.6, cursor: 'not-allowed' }} title="PayOS đang chờ kiểm chứng sandbox">
                      <input type="radio" name="paymentMethod" value="PAYOS" disabled checked={paymentMethod === 'PAYOS'} onChange={() => { setPaymentMethod('PAYOS'); clearCheckoutIntent() }} />
                      💳 PayOS (Sắp mở)
                    </label>
                  </fieldset>

                  <button
                    type="button"
                    className="button-primary checkout-button"
                    onClick={checkout}
                    disabled={!cart.canCheckout || !addressId || checkingOut}
                  >
                    {checkingOut ? 'Đang giữ tồn…' : 'Đặt hàng và giữ tồn'}
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
