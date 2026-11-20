import { useEffect, useMemo, useState } from 'react'
import { getProductSummaries } from '../catalog/catalog.api'
import type { ProductSummary } from '../catalog/catalog.api'
import { ProductCard } from '../catalog/ProductCard'
import { useRecentProducts } from './useRecentProducts'

export interface RecentlyViewedSectionProps {
  excludeProductId?: string
  className?: string
}

export function RecentlyViewedSection({ excludeProductId, className }: RecentlyViewedSectionProps) {
  const { recentIds, clear, prune } = useRecentProducts()
  const [products, setProducts] = useState<ProductSummary[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)

  const effectiveIds = useMemo(() => {
    return excludeProductId ? recentIds.filter(id => id !== excludeProductId) : recentIds
  }, [recentIds, excludeProductId])

  const idsKey = effectiveIds.join(',')
  const [prevIdsKey, setPrevIdsKey] = useState(idsKey)

  if (idsKey !== prevIdsKey) {
    setPrevIdsKey(idsKey)
    if (effectiveIds.length === 0) {
      setProducts([])
      setLoading(false)
      setError(null)
    } else {
      setLoading(true)
      setError(null)
    }
  }

  useEffect(() => {
    if (!idsKey) {
      return
    }

    const ids = idsKey.split(',')
    let active = true
    const controller = new AbortController()

    getProductSummaries(ids, controller.signal)
      .then(result => {
        if (!active) return
        if (result.omittedCount > 0) {
          // The current PDP product is intentionally excluded from hydration, not
          // omitted by the server. Preserve it while pruning only stale candidates.
          prune([
            ...result.items.map(p => p.id),
            ...(excludeProductId ? [excludeProductId] : []),
          ])
        }
        setProducts(result.items)
        setLoading(false)
      })
      .catch((err: unknown) => {
        if (!active || controller.signal.aborted) return
        setError(err instanceof Error ? err.message : 'Không thể tải danh sách sản phẩm đã xem')
        setLoading(false)
      })

    return () => {
      active = false
      controller.abort()
    }
  }, [excludeProductId, idsKey, prune, reloadKey])

  function handleClear() {
    clear()
  }

  function handleRetry() {
    setLoading(true)
    setError(null)
    setReloadKey(k => k + 1)
  }

  if (effectiveIds.length === 0) {
    return null
  }

  if (loading && products.length === 0) {
    return (
      <section
        className={`discovery-section recently-viewed-section ${className ?? ''}`}
        aria-busy="true"
        aria-labelledby="recent-products-heading"
        data-testid="recently-viewed-loading"
      >
        <div className="discovery-header">
          <h2 id="recent-products-heading">Bạn đã xem gần đây</h2>
        </div>
        <p className="sr-only" role="status">Đang tải danh sách sản phẩm đã xem…</p>
        <div className="product-grid discovery-skeleton-grid" aria-hidden="true">
          {Array.from({ length: Math.min(4, effectiveIds.length) }).map((_, i) => (
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

  if (error && products.length === 0) {
    return (
      <section
        className={`discovery-section recently-viewed-section discovery-error ${className ?? ''}`}
        aria-labelledby="recent-products-heading"
        data-testid="recently-viewed-error"
      >
        <div className="discovery-header">
          <h2 id="recent-products-heading">Bạn đã xem gần đây</h2>
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

  if (products.length === 0) {
    return null
  }

  return (
    <section
      className={`discovery-section recently-viewed-section ${className ?? ''}`}
      aria-labelledby="recent-products-heading"
      data-testid="recently-viewed-section"
    >
      <div className="discovery-header">
        <h2 id="recent-products-heading">Bạn đã xem gần đây</h2>
        <button
          type="button"
          className="clear-recent-button"
          onClick={handleClear}
          aria-label="Xóa toàn bộ lịch sử sản phẩm đã xem gần đây"
        >
          Xóa lịch sử
        </button>
      </div>
      <div className="product-grid">
        {products.map(product => (
          <ProductCard key={product.id} product={product} />
        ))}
      </div>
    </section>
  )
}
