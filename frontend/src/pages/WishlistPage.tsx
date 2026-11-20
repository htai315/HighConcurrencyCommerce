import { useEffect, useState } from 'react'
import { Link, useLoaderData, useNavigate } from 'react-router-dom'
import { PageHeading } from '../components/layout/PageHeading'
import { formatPrice } from '../features/catalog/catalog.api'
import { getWishlist, removeFromWishlist } from '../features/wishlist/wishlist.api'
import type { wishlistLoader } from '../features/wishlist/wishlist.api'
import type { WishlistItem, WishlistPage as WishlistPageData } from '../features/wishlist/wishlist.api'
import { SkuSelectModal } from '../features/wishlist/SkuSelectModal'
import { WishlistAlertDialog } from '../features/wishlist/WishlistAlertDialog'
import './wishlist-page.css'

export function WishlistPage() {
  const initial = useLoaderData<typeof wishlistLoader>()
  return (
    <WishlistView
      key={`${initial.page}:${initial.items.map(i => `${i.productId}:${i.availability}`).join(',')}`}
      initial={initial}
    />
  )
}

function WishlistView({ initial }: { initial: WishlistPageData }) {
  const navigate = useNavigate()
  const [pageData, setPageData] = useState<WishlistPageData>(initial)
  const [selectedProductId, setSelectedProductId] = useState<string | null>(null)
  const [busyProductId, setBusyProductId] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<{ kind: 'success' | 'error'; message: string; viewCart?: boolean } | null>(null)

  const totalPages = Math.ceil(pageData.totalCount / pageData.pageSize)

  // Listen to external wishlist changes (e.g. from header or other components)
  useEffect(() => {
    let active = true
    const reload = async () => {
      try {
        const fresh = await getWishlist(pageData.page, pageData.pageSize)
        if (active) setPageData(fresh)
      } catch {
        // Keep current data on background reload error
      }
    }
    window.addEventListener('hcc:wishlist-changed', reload)
    return () => {
      active = false
      window.removeEventListener('hcc:wishlist-changed', reload)
    }
  }, [pageData.page, pageData.pageSize])

  const handleRemove = async (productId: string) => {
    if (busyProductId) return
    setBusyProductId(productId)
    setFeedback(null)
    try {
      await removeFromWishlist(productId)
      const remainingItems = pageData.items.filter(item => item.productId !== productId)
      const nextTotalCount = Math.max(0, pageData.totalCount - 1)
      const lastValidPage = Math.max(1, Math.ceil(nextTotalCount / pageData.pageSize))
      if (pageData.page > lastValidPage) {
        navigate(lastValidPage === 1 ? '/wishlist' : `/wishlist?page=${lastValidPage}`, { replace: true })
        return
      }
      // Optimistically remove item from current page
      setPageData(prev => ({ ...prev, items: remainingItems, totalCount: nextTotalCount }))
      setFeedback({ kind: 'success', message: 'Đã xóa sản phẩm khỏi danh sách yêu thích.' })
    } catch {
      setFeedback({ kind: 'error', message: 'Xóa sản phẩm thất bại. Vui lòng thử lại.' })
    } finally {
      setBusyProductId(null)
    }
  }

  const handleAddedToCart = (productTitle: string) => {
    setFeedback({
      kind: 'success',
      message: `Đã thêm "${productTitle}" vào giỏ hàng!`,
      viewCart: true,
    })
  }

  return (
    <section className="content-page customer-space-content wishlist-page">
      <nav className="breadcrumb" aria-label="Đường dẫn">
        <Link to="/">Trang chủ</Link>
        <span aria-hidden="true"> › </span>
        <span>Danh sách yêu thích</span>
      </nav>

      <PageHeading
        title="Sản phẩm yêu thích"
        description="Theo dõi giá và kiểm tra tình trạng hàng các mẫu sản phẩm bạn đang quan tâm."
        extra={<span className="notification-count">{pageData.totalCount}/500 sản phẩm</span>}
      />

      {pageData.totalCount >= 500 && (
        <div className="wishlist-limit-banner" role="alert">
          ⚠️ Danh sách yêu thích của bạn đã đạt giới hạn 500 sản phẩm. Vui lòng bỏ bớt để có thể lưu thêm sản phẩm mới.
        </div>
      )}

      {feedback && (
        <div className={`wishlist-alert ${feedback.kind}`} role="status">
          <span>{feedback.message}</span>
          {feedback.viewCart && (
            <Link to="/cart" className="wishlist-alert-link">
              Xem giỏ hàng →
            </Link>
          )}
        </div>
      )}

      {pageData.items.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon" aria-hidden="true">❤️</div>
          <h2>{pageData.page > 1 ? 'Trang này không có sản phẩm' : 'Danh sách yêu thích đang trống'}</h2>
          <p>
            {pageData.page > 1
              ? 'Quay về trang đầu để xem danh sách sản phẩm đã lưu.'
              : 'Hãy lưu lại những sản phẩm bạn quan tâm để dễ dàng theo dõi giá và đặt hàng bất cứ lúc nào.'}
          </p>
          <Link
            to={pageData.page > 1 ? '/wishlist' : '/#catalog'}
            className="button-primary"
          >
            {pageData.page > 1 ? 'Về trang đầu' : 'Khám phá sản phẩm'}
          </Link>
        </div>
      ) : (
        <div className="wishlist-grid">
          {pageData.items.map(item => (
            <WishlistCard
              key={item.productId}
              item={item}
              busy={busyProductId === item.productId}
              onRemove={() => handleRemove(item.productId)}
              onOpenCartModal={() => setSelectedProductId(item.productId)}
            />
          ))}
        </div>
      )}

      {(totalPages > 1 || pageData.page > 1) && (
        <nav className="data-pagination" aria-label="Phân trang danh sách yêu thích">
          {pageData.page > 1 && (
            <button
              type="button"
              className="button-secondary"
              onClick={() => navigate(`/wishlist?page=${pageData.page - 1}`)}
            >
              Trang trước
            </button>
          )}
          <span>
            Trang {pageData.page}
            {totalPages >= pageData.page ? ` / ${totalPages}` : ''}
          </span>
          {pageData.page < totalPages && (
            <button
              type="button"
              className="button-secondary"
              onClick={() => navigate(`/wishlist?page=${pageData.page + 1}`)}
            >
              Trang sau
            </button>
          )}
        </nav>
      )}

      <SkuSelectModal
        productId={selectedProductId}
        onClose={() => setSelectedProductId(null)}
        onSuccess={handleAddedToCart}
      />
    </section>
  )
}

function WishlistCard({
  item,
  busy,
  onRemove,
  onOpenCartModal,
}: {
  item: WishlistItem
  busy: boolean
  onRemove: () => void
  onOpenCartModal: () => void
}) {
  const isAvailable = item.availability === 'AVAILABLE'
  const isOutOfStock = item.availability === 'OUT_OF_STOCK'
  const isUnavailable = item.availability === 'PRODUCT_UNAVAILABLE' || !item.product

  const product = item.product

  return (
    <article className="wishlist-card" data-testid={`wishlist-card-${item.productId}`}>
      <div className="wishlist-card-art">
        {product?.imageUrl ? (
          <img
            src={product.imageUrl.startsWith('http') || product.imageUrl.startsWith('/') ? product.imageUrl : `/${product.imageUrl}`}
            alt={product.title}
            className="wishlist-card-img"
            loading="lazy"
          />
        ) : (
          <div className="wishlist-card-placeholder" aria-hidden="true">
            <span>{isUnavailable ? 'Sản phẩm ngừng kinh doanh' : 'Ảnh đang cập nhật'}</span>
          </div>
        )}

        <span
          className={`wishlist-card-badge ${
            isAvailable ? 'available' : isOutOfStock ? 'out-of-stock' : 'unavailable'
          }`}
        >
          {isAvailable ? 'Còn hàng' : isOutOfStock ? 'Tạm hết hàng' : 'Ngừng kinh doanh'}
        </span>
      </div>

      <div className="wishlist-card-body">
        <span className="wishlist-card-category">
          {product?.category?.name ?? (isUnavailable ? 'Không khả dụng' : 'Thiết bị cầu lông')}
        </span>

        <h3 className="wishlist-card-title">
          {product ? (
            <Link to={`/products/${item.productId}`}>{product.title}</Link>
          ) : (
            <span>Sản phẩm ngừng kinh doanh</span>
          )}
        </h3>

        {product && (
          <div className="wishlist-card-rating">
            {product.reviewCount > 0 ? (
              <>
                <span className="star" aria-hidden="true">★</span>
                <strong>{product.averageRating.toFixed(1)}</strong>
                <span>({product.reviewCount})</span>
              </>
            ) : (
              <span>Chưa có đánh giá</span>
            )}
          </div>
        )}

        <div className="wishlist-card-price">
          {product ? (
            <>
              <small>Từ </small>
              {formatPrice(product.minPrice)}
            </>
          ) : (
            <span style={{ color: '#94a3b8', fontSize: 13 }}>—</span>
          )}
        </div>

        <WishlistAlertDialog
          productId={item.productId}
          productTitle={product?.title ?? 'Sản phẩm ngừng kinh doanh'}
          availability={item.availability}
          initialSummary={item.alerts}
          className="wishlist-card-alert-trigger"
        />

        <div className="wishlist-card-actions">
          <button
            type="button"
            className="button-primary"
            onClick={onOpenCartModal}
            disabled={!isAvailable}
            title={!isAvailable ? 'Sản phẩm hiện không thể thêm vào giỏ' : 'Chọn phiên bản để thêm vào giỏ'}
          >
            {isAvailable ? 'Thêm vào giỏ' : isOutOfStock ? 'Hết hàng' : 'Ngừng bán'}
          </button>
          <button
            type="button"
            className="button-secondary"
            onClick={onRemove}
            disabled={busy}
            aria-label={`Bỏ lưu ${product?.title ?? 'sản phẩm'}`}
          >
            {busy ? '…' : 'Bỏ lưu'}
          </button>
        </div>
      </div>
    </article>
  )
}
