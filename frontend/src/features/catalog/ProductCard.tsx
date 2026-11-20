import { useState } from 'react'
import { Link } from 'react-router-dom'
import { formatPrice } from './catalog.api'
import type { ProductSummary } from './catalog.api'
import { WishlistButton } from '../wishlist/WishlistButton'
import { CompareButton } from '../comparison/CompareButton'

export function ProductCard({ product }: { product: ProductSummary }) {
  const [failedImage, setFailedImage] = useState<string | null>(null)
  return (
    <article className="product-card">
      <Link className="product-art" to={`/products/${product.id}`} tabIndex={-1}>
        {product.imageUrl && failedImage !== product.imageUrl ? (
          <img
            src={product.imageUrl.startsWith('http') || product.imageUrl.startsWith('/') ? product.imageUrl : `/${product.imageUrl}`}
            alt={product.title}
            className="product-img"
            loading="lazy"
            decoding="async"
            onError={() => setFailedImage(product.imageUrl)}
          />
        ) : (
          <div className="catalog-placeholder" aria-hidden="true"><svg viewBox="0 0 80 80" fill="none" stroke="currentColor" strokeWidth="2"><ellipse cx="46" cy="28" rx="16" ry="22" transform="rotate(35 46 28)"/><path d="m33 46-17 25m12-18 7 5M38 12l22 20M32 20l24 23M42 8 27 30m25-18L32 41m27-22L40 47"/></svg><span>Ảnh đang cập nhật</span></div>
        )}
        <span className={`card-stock${product.inStock ? '' : ' unavailable'}`}>{product.inStock ? 'Còn hàng' : 'Tạm hết hàng'}</span>
      </Link>
      <WishlistButton
        productId={product.id}
        productTitle={product.title}
        size="sm"
        className="product-card-wishlist"
      />
      <CompareButton
        product={{
          id: product.id,
          title: product.title,
          imageUrl: product.imageUrl,
          categoryId: product.category?.id ?? null,
          categoryName: product.category?.name ?? null,
        }}
        compact
        className="product-card-compare"
      />
      <div className="product-body">
        <span className="product-category">{product.brand?.name ? `${product.brand.name} · ` : ''}{product.category?.name ?? 'Thiết bị cầu lông'}</span>
        <h3><Link to={`/products/${product.id}`}>{product.title}</Link></h3>
        <div className="product-meta">
          <span className="product-price"><small>Từ </small>{formatPrice(product.minPrice)}</span>
          {product.reviewCount > 0 ? (
            <span className="product-card-rating" aria-label={`Đánh giá: ${product.averageRating.toFixed(1)} trên 5 sao`}>
              <span className="star-icon" aria-hidden="true">★</span>
              <strong>{product.averageRating.toFixed(1)}</strong>
              <small>({product.reviewCount})</small>
            </span>
          ) : (
            <span className="product-card-rating unrated" aria-label="Chưa có đánh giá">
              <span className="star-icon" aria-hidden="true">☆</span>
              <small>Chưa có đánh giá</small>
            </span>
          )}
        </div>
        <Link className="card-discover" to={`/products/${product.id}`} aria-label={`Xem ${product.title}`}>Xem sản phẩm <span aria-hidden="true">↗</span></Link>
      </div>
    </article>
  )
}
