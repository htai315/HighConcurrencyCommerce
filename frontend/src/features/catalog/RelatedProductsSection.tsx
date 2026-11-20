import { useEffect, useState } from 'react'
import { getRelatedProducts } from './catalog.api'
import type { ProductSummary } from './catalog.api'
import { ProductCard } from './ProductCard'

export interface RelatedProductsSectionProps {
  productId: string
  limit?: number
}

export function RelatedProductsSection({ productId, limit = 6 }: RelatedProductsSectionProps) {
  const [items, setItems] = useState<ProductSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)

  const [prevProductId, setPrevProductId] = useState(productId)
  if (productId !== prevProductId) {
    setPrevProductId(productId)
    setLoading(true)
    setError(null)
    setItems([])
  }

  useEffect(() => {
    let active = true
    const controller = new AbortController()

    getRelatedProducts(productId, controller.signal, limit)
      .then(response => {
        if (!active) return
        setItems(response.items)
        setLoading(false)
        setError(null)
      })
      .catch((err: unknown) => {
        if (!active || controller.signal.aborted) return
        setError(err instanceof Error ? err.message : 'Không thể tải danh sách sản phẩm tương tự')
        setLoading(false)
      })

    return () => {
      active = false
      controller.abort()
    }
  }, [productId, limit, reloadKey])

  function handleRetry() {
    setLoading(true)
    setError(null)
    setReloadKey(k => k + 1)
  }

  if (loading) {
    return (
      <section
        className="discovery-section related-products-section"
        aria-busy="true"
        aria-labelledby="related-products-heading"
        data-testid="related-products-loading"
      >
        <div className="discovery-header">
          <h2 id="related-products-heading">Sản phẩm tương tự</h2>
        </div>
        <p className="sr-only" role="status">Đang tải danh sách sản phẩm tương tự…</p>
        <div className="product-grid discovery-skeleton-grid" aria-hidden="true">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="product-card skeleton-card">
              <div className="skeleton-box skeleton-art" />
              <div className="skeleton-body">
                <div className="skeleton-box skeleton-line skeleton-tag" />
                <div className="skeleton-box skeleton-line skeleton-title" />
                <div className="skeleton-box skeleton-line skeleton-price" />
              </div>
            </div>
          ))}
        </div>
      </section>
    )
  }

  if (error) {
    return (
      <section
        className="discovery-section related-products-section discovery-error"
        aria-labelledby="related-products-heading"
        data-testid="related-products-error"
      >
        <div className="discovery-header">
          <h2 id="related-products-heading">Sản phẩm tương tự</h2>
        </div>
        <div className="discovery-error-box" role="status">
          <p>{error}</p>
          <button
            type="button"
            className="button-secondary"
            onClick={handleRetry}
          >
            Thử lại
          </button>
        </div>
      </section>
    )
  }

  if (items.length === 0) {
    return null
  }

  return (
    <section
      className="discovery-section related-products-section"
      aria-labelledby="related-products-heading"
      data-testid="related-products-section"
    >
      <div className="discovery-header">
        <h2 id="related-products-heading">Sản phẩm tương tự</h2>
      </div>
      <div className="product-grid">
        {items.map(product => (
          <ProductCard key={product.id} product={product} />
        ))}
      </div>
    </section>
  )
}
