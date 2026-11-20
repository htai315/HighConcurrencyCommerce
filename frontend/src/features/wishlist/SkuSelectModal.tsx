import { useEffect, useRef, useState } from 'react'
import { formatPrice, getProductDetail } from '../catalog/catalog.api'
import type { ProductDetail, ProductSku } from '../catalog/catalog.api'
import { addCustomerCartItem } from '../cart/cart.api'
import './sku-select-modal.css'

interface SkuSelectModalProps {
  productId: string | null
  onClose: () => void
  onSuccess: (productTitle: string) => void
}

export function SkuSelectModal({ productId, onClose, onSuccess }: SkuSelectModalProps) {
  const [product, setProduct] = useState<ProductDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedSkuId, setSelectedSkuId] = useState<string | null>(null)
  const [quantity, setQuantity] = useState(1)
  const [submitting, setSubmitting] = useState(false)
  const dialogRef = useRef<HTMLDivElement>(null)
  const closeButtonRef = useRef<HTMLButtonElement>(null)
  const returnFocusRef = useRef<HTMLElement | null>(null)
  const submittingRef = useRef(submitting)
  const onCloseRef = useRef(onClose)

  useEffect(() => {
    submittingRef.current = submitting
  }, [submitting])

  useEffect(() => {
    onCloseRef.current = onClose
  }, [onClose])

  useEffect(() => {
    let active = true
    if (!productId) {
      queueMicrotask(() => {
        if (!active) return
        setProduct(null)
        setSelectedSkuId(null)
        setError(null)
        setQuantity(1)
      })
      return () => {
        active = false
      }
    }

    queueMicrotask(() => {
      if (!active) return
      setLoading(true)
      setError(null)
    })

    getProductDetail(productId)
      .then(detail => {
        if (!active) return
        setProduct(detail)
        const onlySku = detail.skus.length === 1 ? detail.skus[0] : undefined
        setSelectedSkuId(onlySku?.inStock ? onlySku.id : null)
      })
      .catch(() => {
        if (!active) return
        setError('Không thể tải thông tin sản phẩm. Vui lòng thử lại.')
      })
      .finally(() => {
        if (active) setLoading(false)
      })

    return () => {
      active = false
    }
  }, [productId])

  useEffect(() => {
    if (!productId) return
    returnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    queueMicrotask(() => closeButtonRef.current?.focus())

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !submittingRef.current) {
        event.preventDefault()
        onCloseRef.current()
        return
      }
      if (event.key !== 'Tab' || !dialogRef.current) return

      const focusable = Array.from(
        dialogRef.current.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')
      )
      if (focusable.length === 0) return
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
      returnFocusRef.current?.focus()
      returnFocusRef.current = null
    }
  }, [productId])

  if (!productId) return null

  const selectedSku: ProductSku | undefined = product?.skus.find(s => s.id === selectedSkuId)

  const handleAddToCart = async () => {
    if (!selectedSku || !selectedSku.inStock || submitting) return
    setSubmitting(true)
    try {
      await addCustomerCartItem(selectedSku.id, quantity)
      onSuccess(product?.title ?? 'Sản phẩm')
      onClose()
    } catch {
      setError('Thêm vào giỏ hàng thất bại. Vui lòng thử lại.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={() => { if (!submitting) onClose() }} role="presentation">
      <div
        ref={dialogRef}
        className="sku-modal-content"
        role="dialog"
        aria-modal="true"
        aria-labelledby="sku-modal-title"
        onClick={e => e.stopPropagation()}
      >
        <div className="sku-modal-header">
          <h2 id="sku-modal-title">Chọn phiên bản</h2>
          <button
            ref={closeButtonRef}
            type="button"
            className="sku-modal-close"
            onClick={onClose}
            disabled={submitting}
            aria-label="Đóng cửa sổ"
          >
            ✕
          </button>
        </div>

        {loading ? (
          <div className="sku-modal-body sku-modal-loading" role="status">
            Đang tải thông tin sản phẩm…
          </div>
        ) : error && !product ? (
          <div className="sku-modal-body">
            <p className="sku-modal-error">{error}</p>
            <button type="button" className="button-secondary" onClick={onClose}>
              Đóng
            </button>
          </div>
        ) : product ? (
          <div className="sku-modal-body">
            <div className="sku-modal-product-summary">
              <strong>{product.title}</strong>
              <div className="sku-modal-price">
                {selectedSku ? formatPrice(selectedSku.price) : 'Chọn phân loại'}
              </div>
            </div>

            {error && <p className="sku-modal-error">{error}</p>}

            <div className="sku-modal-options">
              <span className="sku-modal-label">Phiên bản / Phân loại:</span>
              <div className="sku-modal-sku-list" role="radiogroup" aria-label="Danh sách phiên bản">
                {product.skus.map(s => {
                  const isSelected = s.id === selectedSkuId
                  return (
                    <button
                      key={s.id}
                      type="button"
                      role="radio"
                      aria-checked={isSelected}
                      className={`sku-option-btn ${isSelected ? 'active' : ''} ${!s.inStock ? 'out-of-stock' : ''}`}
                      onClick={() => {
                        setSelectedSkuId(s.id)
                        setQuantity(1)
                      }}
                      disabled={!s.inStock || submitting}
                    >
                      <span className="sku-option-label">{s.optionLabel || s.skuCode}</span>
                      <span className="sku-option-price">{formatPrice(s.price)}</span>
                      {!s.inStock && <span className="sku-option-stock">(Hết)</span>}
                    </button>
                  )
                })}
              </div>
            </div>

            <div className="sku-modal-qty-wrap">
              <label htmlFor="sku-modal-qty" className="sku-modal-label">Số lượng:</label>
              <div className="sku-modal-stepper">
                <button
                  type="button"
                  onClick={() => setQuantity(q => Math.max(1, q - 1))}
                  disabled={quantity <= 1 || submitting}
                  aria-label="Giảm số lượng"
                >
                  −
                </button>
                <input
                  id="sku-modal-qty"
                  type="number"
                  min={1}
                  max={99}
                  value={quantity}
                  readOnly
                  aria-label="Số lượng"
                />
                <button
                  type="button"
                  onClick={() => setQuantity(q => Math.min(99, q + 1))}
                  disabled={quantity >= 99 || submitting || (selectedSku ? !selectedSku.inStock : true)}
                  aria-label="Tăng số lượng"
                >
                  +
                </button>
              </div>
            </div>

            <div className="sku-modal-actions">
              <button
                type="button"
                className="button-secondary"
                onClick={onClose}
                disabled={submitting}
              >
                Hủy
              </button>
              <button
                type="button"
                className="button-primary"
                onClick={handleAddToCart}
                disabled={!selectedSku || !selectedSku.inStock || submitting}
              >
                {submitting ? 'Đang thêm…' : selectedSku?.inStock ? 'Thêm vào giỏ hàng' : 'Chọn phiên bản'}
              </button>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )
}
