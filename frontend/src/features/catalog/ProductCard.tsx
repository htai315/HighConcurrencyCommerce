import { Link } from 'react-router-dom'
import { formatPrice } from './catalog.api'
import type { ProductSummary } from './catalog.api'

export function ProductCard({ product }: { product: ProductSummary }) {
  return (
    <article className="product-card">
      <div className="product-art">
        {product.imageUrl ? (
          <img
            src={product.imageUrl.startsWith('http') || product.imageUrl.startsWith('/') ? product.imageUrl : `/${product.imageUrl}`}
            alt={product.title}
            className="product-img"
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="catalog-placeholder" aria-hidden="true"><svg viewBox="0 0 80 80" fill="none" stroke="currentColor" strokeWidth="2"><ellipse cx="46" cy="28" rx="16" ry="22" transform="rotate(35 46 28)"/><path d="m33 46-17 25m12-18 7 5M38 12l22 20M32 20l24 23M42 8 27 30m25-18L32 41m27-22L40 47"/></svg><span>Ảnh đang cập nhật</span></div>
        )}
        <Link className="product-card-arrow" to={`/products/${product.id}`} aria-label={`Xem ${product.title}`}>↗</Link>
      </div>
      <div className="product-body">
        <span className="product-category">{product.category?.name ?? 'Thiết bị cầu lông'}</span>
        <h3><Link to={`/products/${product.id}`}>{product.title}</Link></h3>
        <div className="product-meta">
          <span className="product-price"><small>Từ </small>{formatPrice(product.minPrice)}</span>
          <span className={`stock-label${product.inStock ? '' : ' out-of-stock'}`}>{product.inStock ? 'Còn hàng' : 'Tạm hết hàng'}</span>
        </div>
      </div>
    </article>
  )
}
