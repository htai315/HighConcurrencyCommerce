import { useEffect, useState } from 'react'
import { Link, useLoaderData, useRouteLoaderData, useSearchParams } from 'react-router-dom'
import { formatPrice, productLoader } from '../features/catalog/catalog.api'
import type { ProductDetail } from '../features/catalog/catalog.api'
import { sessionLoader } from '../features/auth/auth.api'
import { addGuestItem } from '../features/cart/guest-cart'
import { addCustomerCartItem } from '../features/cart/cart.api'
import { getPublicFlashSales } from '../features/flash-sale/flash-sale.api'
import type { PublicFlashSaleOffer } from '../features/flash-sale/flash-sale.api'

export function ProductDetailPage() {
  const product = useLoaderData<typeof productLoader>()
  return <ProductDetailView key={product.id} product={product} />
}

function ProductDetailView({ product }: { product: ProductDetail }) {
  const [selectedSkuId, setSelectedSkuId] = useState(product.skus[0]?.id)
  const [selectedImageIndex, setSelectedImageIndex] = useState(0)
  const [cartMessage, setCartMessage] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [searchParams] = useSearchParams()
  const requestedOfferId = searchParams.get('flashSaleOfferId')
  const [flashOffer, setFlashOffer] = useState<PublicFlashSaleOffer | null>(null)
  const [flashOfferLoading, setFlashOfferLoading] = useState(requestedOfferId !== null)
  const user = useRouteLoaderData<typeof sessionLoader>('session')

  useEffect(() => {
    if (!requestedOfferId) return
    const controller = new AbortController()
    getPublicFlashSales(controller.signal)
      .then(campaigns => {
        const offer = campaigns.flatMap(campaign => campaign.offers)
          .find(candidate => candidate.offerId === requestedOfferId && candidate.productId === product.id) ?? null
        setFlashOffer(offer)
        if (offer) setSelectedSkuId(offer.productSkuId)
      })
      .catch(() => setFlashOffer(null))
      .finally(() => setFlashOfferLoading(false))
    return () => controller.abort()
  }, [product.id, requestedOfferId])

  const selectedSku = product.skus.find(sku => sku.id === selectedSkuId) ?? product.skus[0]
  const images = product.images ?? []
  const activeImage = images[selectedImageIndex] ?? images[0]
  const activeFlashOffer = flashOffer?.productSkuId === selectedSku?.id ? flashOffer : null

  const resolveImgSrc = (key: string) => (key.startsWith('http') || key.startsWith('/') ? key : `/${key}`)

  async function addToCart() {
    if (!selectedSku || !selectedSku.inStock) return
    setAdding(true)
    setCartMessage(null)
    try {
      if (user?.role === 'CUSTOMER') await addCustomerCartItem(selectedSku.id, 1, activeFlashOffer?.offerId)
      else addGuestItem({
        productSkuId: selectedSku.id, productId: product.id, productTitle: product.title,
        skuCode: selectedSku.skuCode, optionLabel: selectedSku.optionLabel,
        unitPrice: activeFlashOffer?.salePrice ?? selectedSku.price,
        imageUrl: activeImage?.fileKey ?? null,
        flashSaleOfferId: activeFlashOffer?.offerId ?? null,
      })
      setCartMessage('Đã thêm vào giỏ hàng.')
    } catch { setCartMessage('Chưa thể thêm vào giỏ. Vui lòng thử lại.') }
    finally { setAdding(false) }
  }

  return (
    <section className="content-page product-page">
      <Link className="back-link" to="/">← Trở về cửa hàng</Link>
      <div className="product-detail">
        <div className="detail-art">
          {activeImage ? (
            <div className="product-media-gallery">
              <img
                src={resolveImgSrc(activeImage.fileKey)}
                alt={activeImage.altText || product.title}
                className="detail-main-img"
              />
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
            <div className="catalog-placeholder" aria-hidden="true" style={{ minHeight: 360, borderRadius: 16 }}>Chưa có ảnh</div>
          )}
        </div>

        <div className="product-purchase-panel">
          <p className="eyebrow">{product.category?.name ?? 'HighCommerce'}</p>
          <h1 style={{ fontSize: 'clamp(26px, 3.2vw, 38px)', marginBottom: 12 }}>{product.title}</h1>

          <fieldset className="sku-options">
            <legend>Chọn phiên bản</legend>
            {product.skus.map(sku => (
              <label key={sku.id}>
                <input
                  type="radio"
                  name="sku"
                  checked={selectedSku?.id === sku.id}
                  onChange={() => setSelectedSkuId(sku.id)}
                />
                {sku.optionLabel}
              </label>
            ))}
          </fieldset>

          {selectedSku && (
            <div className="sku-summary" aria-live="polite">
              {activeFlashOffer ? <div className="flash-product-price">
                <span className="badge badge-active">FLASH SALE</span>
                <p className="product-price">{formatPrice(activeFlashOffer.salePrice)}</p>
                <del>{formatPrice(selectedSku.price)}</del>
                <small>Còn {activeFlashOffer.remainingQuota}/{activeFlashOffer.quotaTotal} suất · tối đa {activeFlashOffer.perCustomerLimit}/khách</small>
              </div> : <p className="product-price">{formatPrice(selectedSku.price)}</p>}
              {requestedOfferId && !flashOfferLoading && !flashOffer && <p className="cart-warning">Ưu đãi Flash Sale không còn hiệu lực.</p>}
              <p className={selectedSku.inStock ? 'stock-label' : 'stock-label out-of-stock'} style={{ margin: '8px 0 12px' }}>
                {selectedSku.inStock ? 'Còn hàng' : 'Tạm hết hàng'}
              </p>
              <p style={{ fontSize: 13, color: 'var(--muted)', margin: '0 0 16px' }}>Mã sản phẩm: <strong>{selectedSku.skuCode}</strong></p>
              <button
                type="button"
                className="button-primary add-cart-button"
                onClick={addToCart}
                disabled={!selectedSku.inStock || adding || flashOfferLoading || (requestedOfferId !== null && !activeFlashOffer)}
              >
                {adding ? 'Đang thêm…' : selectedSku.inStock ? 'Thêm vào giỏ' : 'Không thể thêm'}
              </button>
              {cartMessage && <p className="cart-inline-message" role="status">{cartMessage} <Link to="/cart">Xem giỏ</Link></p>}
            </div>
          )}

          <p className="purchase-guidance">Chọn phiên bản phù hợp trước khi thêm vào giỏ. Giá và tình trạng hàng được cập nhật theo phiên bản bạn chọn.</p>

          <h2 style={{ fontSize: 20, marginBottom: 12 }}>Mô tả sản phẩm</h2>
          <p className="product-description">{product.description || 'Sản phẩm chưa có mô tả.'}</p>
        </div>
      </div>
    </section>
  )
}
