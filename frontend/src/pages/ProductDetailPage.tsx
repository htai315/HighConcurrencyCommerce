import { useEffect, useState } from 'react'
import { Link, useLoaderData, useRouteLoaderData, useSearchParams } from 'react-router-dom'
import { formatPrice, productLoader } from '../features/catalog/catalog.api'
import type { ProductDetail, ProductSku } from '../features/catalog/catalog.api'
import { sessionLoader } from '../features/auth/auth.api'
import { addGuestItem } from '../features/cart/guest-cart'
import { addCustomerCartItem } from '../features/cart/cart.api'
import { getPublicFlashSales } from '../features/flash-sale/flash-sale.api'
import type { PublicFlashSaleOffer } from '../features/flash-sale/flash-sale.api'
import {
  type ProductReviewPage,
  type ReviewEligibility,
  getPublicReviews,
  getReviewEligibility,
  createReview,
  updateReview,
  deleteReview,
} from '../features/reviews/reviews.api'
import { RatingSummary } from '../features/reviews/RatingSummary'
import { ReviewForm } from '../features/reviews/ReviewForm'
import { ReviewList } from '../features/reviews/ReviewList'
import { ReviewCard } from '../features/reviews/ReviewCard'
import { ConfirmDialog } from '../components/layout/ConfirmDialog'
import { WishlistButton } from '../features/wishlist/WishlistButton'
import { WishlistAlertDialog } from '../features/wishlist/WishlistAlertDialog'
import { useWishlist } from '../features/wishlist/useWishlist'
import { CompareButton } from '../features/comparison/CompareButton'
import { useRecentProducts } from '../features/recent-products/useRecentProducts'
import { RecentlyViewedSection } from '../features/recent-products/RecentlyViewedSection'
import { RelatedProductsSection } from '../features/catalog/RelatedProductsSection'

export function ProductDetailPage() {
  const product = useLoaderData<typeof productLoader>()
  return <ProductDetailView key={product.id} product={product} />
}

function parseSkuLabel(sku: ProductSku) {
  const parts = sku.optionLabel.split('/').map(p => p.trim())
  if (parts.length >= 3) {
    return {
      color: parts[0],
      material: parts[1],
      size: parts[2],
      hasStructure: true,
    }
  } else if (parts.length === 2) {
    return {
      color: parts[0],
      material: undefined,
      size: parts[1],
      hasStructure: true,
    }
  }
  return {
    color: sku.optionLabel,
    material: undefined,
    size: sku.optionLabel,
    hasStructure: false,
  }
}

function sanitizeExternalUrl(url?: string | null): string | null {
  if (!url) return null
  const trimmed = url.trim()
  if (/^https?:\/\//i.test(trimmed)) {
    return trimmed
  }
  return null
}

function ProductDetailView({ product }: { product: ProductDetail }) {
  const [selectedSkuId, setSelectedSkuId] = useState(product.skus[0]?.id)
  const [selectedImageIndex, setSelectedImageIndex] = useState(0)
  const [quantity, setQuantity] = useState(1)
  const [activeTab, setActiveTab] = useState<'desc' | 'specs'>('desc')
  const [cartMessage, setCartMessage] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [searchParams] = useSearchParams()
  const requestedOfferId = searchParams.get('flashSaleOfferId')
  const [flashOffer, setFlashOffer] = useState<PublicFlashSaleOffer | null>(null)
  const [flashOfferLoading, setFlashOfferLoading] = useState(requestedOfferId !== null)
  const user = useRouteLoaderData<typeof sessionLoader>('session')
  const { isSaved } = useWishlist()
  const { recordView } = useRecentProducts()

  useEffect(() => {
    if (product.id) {
      recordView(product.id)
    }
  }, [product.id, recordView])

  // Review states
  const [reviewsData, setReviewsData] = useState<ProductReviewPage | null>(null)
  const [reviewsLoading, setReviewsLoading] = useState(true)
  const [reviewSort, setReviewSort] = useState<'newest' | 'highest' | 'lowest'>('newest')
  const [selectedRatingFilter, setSelectedRatingFilter] = useState<number | null>(null)
  const [reviewPage, setReviewPage] = useState(1)
  const [eligibility, setEligibility] = useState<ReviewEligibility | null>(null)
  const [isWritingReview, setIsWritingReview] = useState(false)
  const [submittingReview, setSubmittingReview] = useState(false)
  const [reviewFormError, setReviewFormError] = useState<string | null>(null)
  const [reviewActionMessage, setReviewActionMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [confirmDeleteReviewId, setConfirmDeleteReviewId] = useState<string | null>(null)
  const [reviewsReloadKey, setReviewsReloadKey] = useState(0)

  useEffect(() => {
    let ignore = false
    getPublicReviews(product.id, {
      sort: reviewSort,
      rating: selectedRatingFilter ?? undefined,
      page: reviewPage,
      pageSize: 10,
    })
      .then(data => {
        if (!ignore) setReviewsData(data)
      })
      .catch(() => {})
      .finally(() => {
        if (!ignore) setReviewsLoading(false)
      })
    return () => {
      ignore = true
    }
  }, [product.id, reviewSort, selectedRatingFilter, reviewPage, reviewsReloadKey])

  useEffect(() => {
    if (user?.role !== 'CUSTOMER') return
    let ignore = false
    getReviewEligibility(product.id)
      .then(el => {
        if (!ignore) setEligibility(el)
      })
      .catch(() => {
        if (!ignore) setEligibility(null)
      })
    return () => {
      ignore = true
    }
  }, [product.id, user?.role, reviewsReloadKey])

  async function handleSubmitReview(data: { rating: number; content: string }) {
    setSubmittingReview(true)
    setReviewFormError(null)
    setReviewActionMessage(null)
    try {
      if (eligibility?.existingReview) {
        await updateReview(eligibility.existingReview.id, data)
        setReviewActionMessage({ type: 'success', text: 'Cập nhật đánh giá thành công!' })
      } else {
        await createReview(product.id, data)
        setReviewActionMessage({ type: 'success', text: 'Cảm ơn bạn đã gửi đánh giá sản phẩm!' })
      }
      setIsWritingReview(false)
      setReviewsLoading(true)
      setReviewsReloadKey(k => k + 1)
    } catch (err: unknown) {
      setReviewFormError(err instanceof Error ? err.message : 'Gửi đánh giá thất bại. Vui lòng thử lại.')
    } finally {
      setSubmittingReview(false)
    }
  }

  async function handleDeleteReview(reviewId: string) {
    setSubmittingReview(true)
    setReviewActionMessage(null)
    try {
      await deleteReview(reviewId)
      setReviewActionMessage({ type: 'success', text: 'Đã xóa đánh giá của bạn.' })
      setConfirmDeleteReviewId(null)
      setIsWritingReview(false)
      setReviewsLoading(true)
      setReviewsReloadKey(k => k + 1)
    } catch (err: unknown) {
      setReviewActionMessage({
        type: 'error',
        text: err instanceof Error ? err.message : 'Xóa đánh giá thất bại.',
      })
    } finally {
      setSubmittingReview(false)
    }
  }

  useEffect(() => {
    if (!requestedOfferId) return
    const controller = new AbortController()
    getPublicFlashSales(controller.signal)
      .then(campaigns => {
        if (controller.signal.aborted) return
        const offer = campaigns.flatMap(campaign => campaign.offers)
          .find(candidate => candidate.offerId === requestedOfferId && candidate.productId === product.id) ?? null
        setFlashOffer(offer)
        if (offer) setSelectedSkuId(offer.productSkuId)
      })
      .catch(() => {
        if (!controller.signal.aborted) setFlashOffer(null)
      })
      .finally(() => {
        if (!controller.signal.aborted) setFlashOfferLoading(false)
      })
    return () => controller.abort()
  }, [product.id, requestedOfferId])

  const selectedSku = product.skus.find(sku => sku.id === selectedSkuId) ?? product.skus[0]
  const images = product.images ?? []
  const activeImage = images[selectedImageIndex] ?? images[0]
  const activeFlashOffer = requestedOfferId && flashOffer?.offerId === requestedOfferId && flashOffer?.productSkuId === selectedSku?.id ? flashOffer : null

  // Phân tích cấu trúc các biến thể SKU
  const parsedSkus = product.skus.map(sku => ({
    sku,
    ...parseSkuLabel(sku),
  }))
  const allStructured = parsedSkus.every(p => p.hasStructure)
  const distinctColors = Array.from(new Set(parsedSkus.map(p => p.color)))
  const selectedParsed = parsedSkus.find(p => p.sku.id === selectedSku?.id)
  const selectedColor = selectedParsed?.color ?? distinctColors[0]

  // Lọc danh sách kích cỡ theo màu đang chọn (nếu có nhiều màu)
  const availableSkusForColor = distinctColors.length > 1
    ? parsedSkus.filter(p => p.color === selectedColor)
    : parsedSkus

  const resolveImgSrc = (key: string) => (key.startsWith('http') || key.startsWith('/') ? key : `/${key}`)

  async function addToCart() {
    if (!selectedSku || !selectedSku.inStock) return
    setAdding(true)
    setCartMessage(null)
    try {
      if (user?.role === 'CUSTOMER') {
        await addCustomerCartItem(selectedSku.id, quantity, activeFlashOffer?.offerId)
      } else {
        addGuestItem({
          productSkuId: selectedSku.id,
          productId: product.id,
          productTitle: product.title,
          skuCode: selectedSku.skuCode,
          optionLabel: selectedSku.optionLabel,
          unitPrice: activeFlashOffer?.salePrice ?? selectedSku.price,
          imageUrl: activeImage?.fileKey ?? null,
          flashSaleOfferId: activeFlashOffer?.offerId ?? null,
        }, quantity)
      }
      setCartMessage('Đã thêm vào giỏ hàng.')
    } catch {
      setCartMessage('Chưa thể thêm vào giỏ. Vui lòng thử lại.')
    } finally {
      setAdding(false)
    }
  }

  return (
    <section className="content-page product-page">
      {/* Breadcrumb định vị */}
      <nav className="product-breadcrumb" aria-label="Đường dẫn sản phẩm">
        <Link to="/">Trang chủ</Link>
        <span aria-hidden="true">/</span>
        {product.category && (
          <>
            <Link to={`/?categoryId=${encodeURIComponent(product.category.id)}#catalog`}>
              {product.category.name}
            </Link>
            <span aria-hidden="true">/</span>
          </>
        )}
        <span aria-current="page">{product.title}</span>
      </nav>

      <div className="product-detail">
        {/* Gallery hình ảnh bên trái */}
        <div className="detail-art">
          {activeImage ? (
            <div className="product-media-gallery">
              <div className="main-image-viewport">
                <img
                  src={resolveImgSrc(activeImage.fileKey)}
                  alt={activeImage.altText || product.title}
                  className="detail-main-img"
                />
                {activeFlashOffer && (
                  <span className="gallery-badge flash-badge">FLASH SALE</span>
                )}
              </div>
              {images.length > 1 && (
                <div className="media-thumbnails" role="group" aria-label="Hình ảnh sản phẩm">
                  {images.map((img, idx) => (
                    <button
                      key={img.id}
                      type="button"
                      className={`thumb-btn ${idx === selectedImageIndex ? 'active' : ''}`}
                      onClick={() => setSelectedImageIndex(idx)}
                      aria-label={`Xem ảnh ${idx + 1}`}
                      aria-pressed={idx === selectedImageIndex}
                    >
                      <img src={resolveImgSrc(img.fileKey)} alt="" />
                    </button>
                  ))}
                </div>
              )}
            </div>
          ) : (
            <div className="catalog-placeholder" aria-hidden="true" style={{ minHeight: 380, borderRadius: 16 }}>
              <span>Chưa có ảnh sản phẩm</span>
            </div>
          )}
        </div>

        {/* Khung đặt hàng bên phải */}
        <div className="product-purchase-panel">
          <div className="product-header-meta">
            <span className="eyebrow-pill">{product.category?.name ?? 'Thiết bị cầu lông'}</span>
            {product.brand && <span className="eyebrow-pill">{product.brand.name}{product.brand.country ? ` · ${product.brand.country}` : ''}</span>}
            <span className="sku-code-chip">SKU: <strong>{selectedSku?.skuCode}</strong></span>
          </div>

          <h1 className="product-detail-title">{product.title}</h1>

          <div className="product-rating-header-row">
            {(reviewsData?.summary.reviewCount ?? product.reviewCount) > 0 ? (
              <a href="#reviews" className="product-header-rating-anchor">
                <span className="star-icon" aria-hidden="true">★</span>
                <strong>{(reviewsData?.summary.averageRating ?? product.averageRating).toFixed(1)}</strong>
                <span className="header-review-count">
                  ({reviewsData?.summary.reviewCount ?? product.reviewCount} đánh giá)
                </span>
              </a>
            ) : (
              <a href="#reviews" className="product-header-rating-anchor unrated">
                <span className="star-icon" aria-hidden="true">☆</span>
                <span className="header-review-count">Chưa có đánh giá</span>
              </a>
            )}
          </div>

          {/* Khối giá bán nổi bật */}
          <div className="product-pricing-box">
            {activeFlashOffer ? (
              <div className="flash-product-price">
                <div className="flash-sale-ribbon">
                  <span className="badge badge-active">FLASH SALE</span>
                  <span className="flash-discount-tag">
                    Tiết kiệm {formatPrice((selectedSku?.price ?? 0) - activeFlashOffer.salePrice)}
                  </span>
                </div>
                <div className="price-display-row">
                  <p className="product-price highlight">{formatPrice(activeFlashOffer.salePrice)}</p>
                  <del className="original-price">{formatPrice(selectedSku?.price ?? 0)}</del>
                </div>
                <div className="flash-quota-bar">
                  <small>
                    Còn <strong>{activeFlashOffer.remainingQuota}</strong> / {activeFlashOffer.quotaTotal} suất ưu đãi
                    (tối đa {activeFlashOffer.perCustomerLimit} chiếc/khách)
                  </small>
                </div>
              </div>
            ) : (
              <div className="price-display-row">
                <p className="product-price">{formatPrice(selectedSku?.price ?? 0)}</p>
                <span className="vat-badge">Đã gồm VAT</span>
              </div>
            )}
            {requestedOfferId && !flashOfferLoading && !flashOffer && (
              <p className="cart-warning">⚠️ Ưu đãi Flash Sale này không còn hiệu lực.</p>
            )}
          </div>

          {/* Bộ chọn phiên bản (SKU / Kích thước) chuyên nghiệp */}
          <fieldset className="sku-options">
            <legend className="sku-legend-title">Chọn phiên bản</legend>

            {allStructured ? (
              <div className="structured-variant-container">
                {/* Thuộc tính chung nếu có (Chất liệu / Màu sắc) */}
                {selectedParsed?.material && (
                  <div className="variant-common-attributes">
                    <span className="attr-icon" aria-hidden="true">🏷️</span>
                    <span><strong>Chất liệu:</strong> {selectedParsed.material}</span>
                  </div>
                )}

                {/* Chọn màu nếu sản phẩm có nhiều màu sắc */}
                {distinctColors.length > 1 && (
                  <div className="color-selector-block">
                    <div className="variant-label-row">
                      <span>Màu sắc:</span>
                      <strong>{selectedColor}</strong>
                    </div>
                    <div className="color-chips-row">
                      {distinctColors.map(color => {
                        const matchingSku = parsedSkus.find(p => p.color === color)?.sku
                        const isColorActive = selectedColor === color
                        return (
                          <button
                            key={color}
                            type="button"
                            className={`color-chip-btn ${isColorActive ? 'active' : ''}`}
                            onClick={() => {
                              if (matchingSku) setSelectedSkuId(matchingSku.id)
                            }}
                          >
                            {color}
                          </button>
                        )
                      })}
                    </div>
                  </div>
                )}

                {/* Grid kích cỡ gọn gàng, trực quan */}
                <div className="size-selector-block">
                  <div className="variant-label-row">
                    <span>Chọn kích cỡ / phân loại:</span>
                    <strong>{selectedParsed?.size}</strong>
                  </div>

                  <div className="size-pills-grid" role="radiogroup" aria-label="Chọn kích cỡ">
                    {availableSkusForColor.map(item => {
                      const isSelected = selectedSku?.id === item.sku.id
                      const inStock = item.sku.inStock
                      return (
                        <label
                          key={item.sku.id}
                          className={`size-pill ${isSelected ? 'active' : ''} ${!inStock ? 'out-of-stock' : ''}`}
                          title={item.sku.optionLabel}
                        >
                          <input
                            type="radio"
                            name="sku"
                            checked={isSelected}
                            onChange={() => setSelectedSkuId(item.sku.id)}
                            aria-label={item.sku.optionLabel}
                            className="visually-hidden-radio"
                          />
                          <span className="size-pill-text">{item.size}</span>
                          {!inStock && <span className="size-oos-mark" title="Tạm hết hàng">×</span>}
                        </label>
                      )
                    })}
                  </div>
                </div>
              </div>
            ) : (
              /* Trường hợp phiên bản đơn giản (như Tiêu chuẩn / Nâng cấp) */
              <div className="option-chips-grid" role="radiogroup" aria-label="Chọn phiên bản">
                {product.skus.map(sku => {
                  const isSelected = selectedSku?.id === sku.id
                  return (
                    <label
                      key={sku.id}
                      className={`option-chip ${isSelected ? 'active' : ''} ${!sku.inStock ? 'out-of-stock' : ''}`}
                    >
                      <input
                        type="radio"
                        name="sku"
                        checked={isSelected}
                        onChange={() => setSelectedSkuId(sku.id)}
                        aria-label={sku.optionLabel}
                        className="visually-hidden-radio"
                      />
                      <div className="chip-content">
                        <span className="chip-label">{sku.optionLabel}</span>
                        <span className="chip-price">{formatPrice(sku.price)}</span>
                      </div>
                      {!sku.inStock && <span className="chip-status-tag">Hết hàng</span>}
                    </label>
                  )
                })}
              </div>
            )}
          </fieldset>

          {/* Trạng thái tồn kho và số lượng */}
          {selectedSku && (
            <div className="sku-summary" aria-live="polite">
              <div className="stock-status-banner">
                <span className={`stock-status-indicator ${selectedSku.inStock ? 'is-available' : 'is-empty'}`}>
                  {selectedSku.inStock ? 'Còn hàng' : 'Tạm hết hàng'}
                </span>
                <span className="delivery-fast-note">Sẵn sàng giao hỏa tốc 2-4 ngày</span>
              </div>

              {/* Bộ chọn số lượng */}
              <div className="product-quantity-row">
                <span className="quantity-label">Số lượng:</span>
                <div className="quantity-stepper">
                  <button
                    type="button"
                    className="stepper-btn"
                    onClick={() => setQuantity(q => Math.max(1, q - 1))}
                    disabled={quantity <= 1 || !selectedSku.inStock}
                    aria-label="Giảm số lượng"
                  >
                    −
                  </button>
                  <input
                    type="number"
                    min="1"
                    max="99"
                    value={quantity}
                    onChange={e => {
                      const val = parseInt(e.target.value, 10)
                      if (!isNaN(val)) setQuantity(Math.min(99, Math.max(1, val)))
                    }}
                    aria-label="Số lượng sản phẩm"
                    className="stepper-input"
                  />
                  <button
                    type="button"
                    className="stepper-btn"
                    onClick={() => setQuantity(q => Math.min(99, q + 1))}
                    disabled={quantity >= 99 || !selectedSku.inStock}
                    aria-label="Tăng số lượng"
                  >
                    +
                  </button>
                </div>
              </div>

              {/* Nút thêm giỏ hàng chính & lưu yêu thích */}
              <div className="action-buttons-wrap">
                <button
                  type="button"
                  className="button-primary add-cart-button"
                  onClick={addToCart}
                  disabled={!selectedSku.inStock || adding || flashOfferLoading || (requestedOfferId !== null && !activeFlashOffer)}
                >
                  {adding ? 'Đang thêm…' : selectedSku.inStock ? 'Thêm vào giỏ' : 'Không thể thêm'}
                </button>
                <WishlistButton
                  productId={product.id}
                  productTitle={product.title}
                  size="lg"
                  showText
                  className="pdp-wishlist-btn"
                />
                <CompareButton
                  product={{
                    id: product.id,
                    title: product.title,
                    imageUrl: product.images[0]?.fileKey ?? null,
                    categoryId: product.category?.id ?? null,
                    categoryName: product.category?.name ?? null,
                  }}
                />
              </div>
              {user?.role === 'CUSTOMER' && (
                <div className="pdp-alert-settings">
                  {isSaved(product.id) ? (
                    <WishlistAlertDialog
                      productId={product.id}
                      productTitle={product.title}
                      availability={product.skus.some(sku => sku.inStock) ? 'AVAILABLE' : 'OUT_OF_STOCK'}
                    />
                  ) : (
                    <p>Lưu sản phẩm vào yêu thích để bật thông báo có hàng hoặc giảm giá.</p>
                  )}
                </div>
              )}

              {cartMessage && (
                <div className="cart-inline-message" role="status">
                  <span>✓ {cartMessage}</span>
                  <Link to="/cart" className="view-cart-link">Xem giỏ</Link>
                </div>
              )}
            </div>
          )}

          {/* Cam kết bán hàng uy tín */}
          <div className="product-trust-badges">
            <div className="trust-badge-item">
              <span className="trust-badge-icon" aria-hidden="true">🛡️</span>
              <div>
                <strong>100% Chính hãng</strong>
                <span>Cam kết chuẩn thi đấu quốc tế</span>
              </div>
            </div>
            <div className="trust-badge-item">
              <span className="trust-badge-icon" aria-hidden="true">🔄</span>
              <div>
                <strong>Đổi size 7 ngày</strong>
                <span>Hỗ trợ đổi trả khi chưa sử dụng</span>
              </div>
            </div>
            <div className="trust-badge-item">
              <span className="trust-badge-icon" aria-hidden="true">🚚</span>
              <div>
                <strong>Giao hàng toàn quốc</strong>
                <span>Đồng kiểm COD khi nhận hàng</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Tabs mô tả chi tiết & thông số kỹ thuật */}
      <section className="product-info-tabs-section">
        <div className="tabs-nav" role="tablist">
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'desc'}
            className={`tab-nav-btn ${activeTab === 'desc' ? 'active' : ''}`}
            onClick={() => setActiveTab('desc')}
          >
            Mô tả chi tiết
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'specs'}
            className={`tab-nav-btn ${activeTab === 'specs' ? 'active' : ''}`}
            onClick={() => setActiveTab('specs')}
          >
            Thông số kỹ thuật
          </button>
        </div>

        <div className="tab-body">
          {activeTab === 'desc' ? (
            <div className="product-description-content">
              <p className="product-description">{product.description || 'Sản phẩm chưa có mô tả chi tiết từ cửa hàng.'}</p>
            </div>
          ) : (
            <div className="specs-table-wrapper">
              <table className="specs-table">
                <tbody>
                  <tr>
                    <th>Tên sản phẩm</th>
                    <td>{product.title}</td>
                  </tr>
                  <tr>
                    <th>Danh mục</th>
                    <td>{product.category?.name ?? 'Thiết bị cầu lông'}</td>
                  </tr>
                  {product.brand && (
                    <tr>
                      <th>Thương hiệu</th>
                      <td>
                        {(() => {
                          const safeUrl = sanitizeExternalUrl(product.brand.websiteUrl)
                          return safeUrl ? (
                            <a href={safeUrl} target="_blank" rel="noreferrer">
                              {product.brand.name}
                            </a>
                          ) : (
                            product.brand.name
                          )
                        })()}
                      </td>
                    </tr>
                  )}
                  {product.specifications.map(spec => <tr key={spec.key}><th>{spec.displayName}</th><td>{spec.value}</td></tr>)}
                  <tr>
                    <th>Mã SKU đang chọn</th>
                    <td><code>{selectedSku?.skuCode}</code></td>
                  </tr>
                  {selectedParsed?.color && (
                    <tr>
                      <th>Màu sắc</th>
                      <td>{selectedParsed.color}</td>
                    </tr>
                  )}
                  {selectedParsed?.material && (
                    <tr>
                      <th>Chất liệu / Công nghệ</th>
                      <td>{selectedParsed.material}</td>
                    </tr>
                  )}
                  {selectedParsed?.size && (
                    <tr>
                      <th>Kích cỡ (Size)</th>
                      <td>{selectedParsed.size}</td>
                    </tr>
                  )}
                  <tr>
                    <th>Tình trạng tồn kho</th>
                    <td>{selectedSku?.inStock ? 'Sẵn sàng giao hàng' : 'Đang chờ nhập thêm'}</td>
                  </tr>
                  <tr>
                    <th>Chính sách bảo hành</th>
                    <td>Chính hãng theo tiêu chuẩn của nhà sản xuất</td>
                  </tr>
                </tbody>
              </table>
            </div>
          )}
        </div>
      </section>

      {confirmDeleteReviewId && (
        <ConfirmDialog
          title="Xóa đánh giá?"
          description="Bạn có chắc chắn muốn xóa bài đánh giá này không? Sau khi xóa, bạn có thể viết lại đánh giá mới."
          confirmLabel="Xóa đánh giá"
          busy={submittingReview}
          onCancel={() => setConfirmDeleteReviewId(null)}
          onConfirm={() => handleDeleteReview(confirmDeleteReviewId)}
        />
      )}

      {/* Đánh giá & Nhận xét sản phẩm */}
      <section id="reviews" className="product-reviews-section">
        <div className="reviews-section-header">
          <h2>Đánh giá & Nhận xét</h2>
          <span className="reviews-header-badge">
            {reviewsData ? `${reviewsData.summary.reviewCount} đánh giá` : `${product.reviewCount} đánh giá`}
          </span>
        </div>

        {reviewActionMessage && (
          <div className={`alert alert-${reviewActionMessage.type === 'success' ? 'success' : 'danger'}`} role="status">
            <span>{reviewActionMessage.text}</span>
          </div>
        )}

        {reviewsData && (
          <RatingSummary
            summary={reviewsData.summary}
            selectedRating={selectedRatingFilter}
            onSelectRating={r => {
              setSelectedRatingFilter(r)
              setReviewPage(1)
            }}
          />
        )}

        {/* Khối tương tác đánh giá của người dùng */}
        <div className="user-review-action-area">
          {user?.role === 'CUSTOMER' ? (
            eligibility?.existingReview ? (
              <div className="my-existing-review-box">
                <div className="my-review-header">
                  <h3>Đánh giá của bạn</h3>
                  {!isWritingReview && (
                    <div className="my-review-buttons">
                      <button
                        type="button"
                        className="button secondary btn-sm"
                        onClick={() => setIsWritingReview(true)}
                      >
                        Chỉnh sửa
                      </button>
                      <button
                        type="button"
                        className="button danger btn-sm"
                        onClick={() => setConfirmDeleteReviewId(eligibility.existingReview!.id)}
                      >
                        Xóa
                      </button>
                    </div>
                  )}
                </div>
                {isWritingReview ? (
                  <ReviewForm
                    isEditing
                    initialRating={eligibility.existingReview.rating}
                    initialContent={eligibility.existingReview.content}
                    submitting={submittingReview}
                    errorMessage={reviewFormError}
                    onSubmit={handleSubmitReview}
                    onCancel={() => {
                      setIsWritingReview(false)
                      setReviewFormError(null)
                    }}
                  />
                ) : (
                  <ReviewCard
                    review={eligibility.existingReview}
                    isOwner
                  />
                )}
              </div>
            ) : eligibility?.eligible ? (
              <div className="write-review-prompt-box">
                {!isWritingReview ? (
                  <div className="eligible-review-invite">
                    <div>
                      <strong>Bạn đã mua và nhận sản phẩm này!</strong>
                      <p>Hãy chia sẻ đánh giá của bạn để giúp khách hàng khác có thêm thông tin hữu ích.</p>
                    </div>
                    <button
                      type="button"
                      className="button primary open-review-form-btn"
                      onClick={() => setIsWritingReview(true)}
                    >
                      ★ Viết đánh giá
                    </button>
                  </div>
                ) : (
                  <ReviewForm
                    submitting={submittingReview}
                    errorMessage={reviewFormError}
                    onSubmit={handleSubmitReview}
                    onCancel={() => {
                      setIsWritingReview(false)
                      setReviewFormError(null)
                    }}
                  />
                )}
              </div>
            ) : eligibility?.reasonCode === 'NO_DELIVERED_ORDER' ? (
              <div className="review-notice-banner info">
                <span>ℹ️ Chỉ khách hàng đã mua và nhận hàng thành công mới có thể đánh giá sản phẩm này.</span>
              </div>
            ) : null
          ) : (
            <div className="review-notice-banner auth-prompt">
              <span>
                Vui lòng <Link to="/login">đăng nhập tài khoản mua hàng</Link> để viết đánh giá cho sản phẩm này.
              </span>
            </div>
          )}
        </div>

        {/* Danh sách đánh giá công khai */}
        <ReviewList
          reviews={reviewsData?.items ?? []}
          totalCount={reviewsData?.totalCount ?? 0}
          currentPage={reviewPage}
          pageSize={10}
          sort={reviewSort}
          selectedRating={selectedRatingFilter}
          loading={reviewsLoading}
          onSortChange={sort => {
            setReviewSort(sort)
            setReviewPage(1)
          }}
          onPageChange={setReviewPage}
          onRatingFilterChange={r => {
            setSelectedRatingFilter(r)
            setReviewPage(1)
          }}
        />
      </section>

      {/* Khối sản phẩm tương tự */}
      <RelatedProductsSection productId={product.id} />

      {/* Khối sản phẩm đã xem gần đây */}
      <RecentlyViewedSection excludeProductId={product.id} />
    </section>
  )
}
