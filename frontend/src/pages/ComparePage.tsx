import { useEffect, useMemo, useRef } from 'react'
import type { ReactNode } from 'react'
import { Link, useLoaderData, useNavigate, useSearchParams } from 'react-router-dom'
import { formatPrice } from '../features/catalog/catalog.api'
import type { ProductDetail } from '../features/catalog/catalog.api'
import { comparisonLoader } from '../features/comparison/comparison.api'
import { buildComparisonUrl, resolveComparisonImageUrl } from '../features/comparison/comparison-storage'
import type { ComparisonItem } from '../features/comparison/comparison-storage'
import { useComparison } from '../features/comparison/useComparison'
import './compare-page.css'

type MatrixRow = {
  key: string
  label: string
  values: string[]
  different: boolean
}

function imageUrl(product: ProductDetail): string | null {
  return resolveComparisonImageUrl(product.images[0]?.fileKey)
}

function storageItem(product: ProductDetail): ComparisonItem {
  return {
    id: product.id,
    title: product.title,
    imageUrl: product.images[0]?.fileKey ?? null,
    categoryId: product.category?.id ?? null,
    categoryName: product.category?.name ?? null,
  }
}

function valuesDiffer(values: readonly string[]): boolean {
  return new Set(values.map(value => value.trim().toLocaleLowerCase('vi-VN'))).size > 1
}

function minPrice(product: ProductDetail): number {
  if (!product.skus || product.skus.length === 0) return 0
  return Math.min(...product.skus.map(sku => sku.price))
}

function buildRows(products: ProductDetail[]): MatrixRow[] {
  const core: Omit<MatrixRow, 'different'>[] = [
    { key: 'brand', label: 'Thương hiệu', values: products.map(product => product.brand?.name ?? '—') },
    { key: 'price', label: 'Giá thường từ', values: products.map(product => formatPrice(minPrice(product))) },
    { key: 'stock', label: 'Tình trạng', values: products.map(product => product.skus.some(sku => sku.inStock) ? 'Còn hàng' : 'Tạm hết hàng') },
    { key: 'rating', label: 'Đánh giá', values: products.map(product => product.reviewCount > 0 ? `${product.averageRating.toFixed(1)}/5 (${product.reviewCount})` : 'Chưa có đánh giá') },
    { key: 'skus', label: 'Phiên bản đang bán', values: products.map(product => product.skus.map(sku => `${sku.optionLabel} — ${formatPrice(sku.price)}${sku.inStock ? '' : ' (hết hàng)'}`).join(' · ')) },
  ]

  const specifications = new Map<string, { label: string; order: number }>()
  for (const product of products) {
    for (const specification of product.specifications) {
      const existing = specifications.get(specification.key)
      if (!existing || specification.displayOrder < existing.order) {
        specifications.set(specification.key, { label: specification.displayName, order: specification.displayOrder })
      }
    }
  }
  const specRows: Omit<MatrixRow, 'different'>[] = [...specifications.entries()]
    .sort((left, right) => left[1].order - right[1].order || left[0].localeCompare(right[0]))
    .map(([key, metadata]) => ({
      key: `spec:${key}`,
      label: metadata.label,
      values: products.map(product => product.specifications.find(specification => specification.key === key)?.value ?? '—'),
    }))

  return [...core, ...specRows].map(row => ({ ...row, different: valuesDiffer(row.values) }))
}

export function ComparePage() {
  const { comparison } = useLoaderData<typeof comparisonLoader>()
  const products = comparison.items
  const { replace, remove } = useComparison()
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const removeButtons = useRef(new Map<string, HTMLButtonElement>())
  const pendingFocusIndex = useRef<number | null>(null)
  const differentOnly = searchParams.get('different') === 'true'
  const rows = useMemo(() => buildRows(products), [products])
  const visibleRows = differentOnly ? rows.filter(row => row.different) : rows

  useEffect(() => {
    replace(products.map(storageItem))
  }, [products, replace])

  useEffect(() => {
    if (pendingFocusIndex.current === null || products.length === 0) return
    const target = products[Math.min(pendingFocusIndex.current, products.length - 1)]
    pendingFocusIndex.current = null
    if (target) removeButtons.current.get(target.id)?.focus()
  }, [products])

  function toggleDifferences() {
    const next = new URLSearchParams(searchParams)
    if (differentOnly) next.delete('different')
    else next.set('different', 'true')
    setSearchParams(next, { replace: true })
  }

  function removeProduct(productId: string) {
    pendingFocusIndex.current = products.findIndex(product => product.id === productId)
    remove(productId)
    const remaining = products.filter(product => product.id !== productId).map(storageItem)
    const destination = new URL(buildComparisonUrl(remaining), window.location.origin)
    if (differentOnly) destination.searchParams.set('different', 'true')
    void navigate(`${destination.pathname}${destination.search}`)
  }

  if (products.length === 0) {
    return (
      <section className="compare-page compare-empty">
        <span className="compare-eyebrow">So sánh trang bị</span>
        <h1>Chưa có sản phẩm để so sánh</h1>
        <p>Chọn từ hai sản phẩm cùng danh mục để xem giá, phiên bản và thông số cạnh nhau.</p>
        <Link className="button-primary" to="/#catalog">Khám phá sản phẩm</Link>
      </section>
    )
  }

  return (
    <section className="compare-page">
      <nav className="compare-breadcrumb" aria-label="Breadcrumb"><Link to="/">Cửa hàng</Link><span>/</span><span>So sánh sản phẩm</span></nav>
      <header className="compare-header">
        <div>
          <span className="compare-eyebrow">So sánh trang bị</span>
          <h1>Chọn đúng gear cho lối chơi của bạn</h1>
          <p>{products.length} sản phẩm · Dữ liệu giá và tồn kho được cập nhật khi mở trang.</p>
        </div>
        <label className="compare-difference-toggle">
          <input type="checkbox" checked={differentOnly} onChange={toggleDifferences} />
          <span>Chỉ hiện điểm khác nhau</span>
        </label>
      </header>

      {comparison.omittedCount > 0 && (
        <p className="compare-warning" role="status">
          {comparison.omittedCount} sản phẩm trong liên kết không còn khả dụng và đã được loại khỏi bảng.
        </p>
      )}
      {products.length === 1 && (
        <p className="compare-guidance" role="status">Hãy thêm ít nhất một sản phẩm cùng danh mục để so sánh trực quan hơn.</p>
      )}

      <div className="compare-table-scroll" role="region" aria-label="Bảng so sánh sản phẩm" tabIndex={0}>
        <table className="compare-table">
          <caption>So sánh giá thường, tồn kho, đánh giá, phiên bản và thông số kỹ thuật</caption>
          <thead>
            <tr>
              <th scope="col" className="compare-attribute-column">Tiêu chí</th>
              {products.map(product => (
                <th scope="col" key={product.id}>
                  <article className="compare-product-heading">
                    {imageUrl(product) ? <img src={imageUrl(product)!} alt={product.title} /> : <span className="compare-image-placeholder" aria-hidden="true">↔</span>}
                    <small>{product.brand?.name ?? product.category?.name ?? 'Thiết bị cầu lông'}</small>
                    <h2><Link to={`/products/${product.id}`}>{product.title}</Link></h2>
                    <strong>{formatPrice(minPrice(product))}</strong>
                    <button
                      ref={node => { if (node) removeButtons.current.set(product.id, node); else removeButtons.current.delete(product.id) }}
                      type="button"
                      onClick={() => removeProduct(product.id)}
                      aria-label={`Bỏ ${product.title} khỏi so sánh`}
                    >Bỏ sản phẩm</button>
                  </article>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visibleRows.map(row => (
              <tr key={row.key} className={row.different ? 'is-different' : ''}>
                <th scope="row">{row.label}{row.different && <span className="difference-label">Khác nhau</span>}</th>
                {row.values.map((value, index) => <td key={products[index]?.id}>{value as ReactNode}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
        {differentOnly && visibleRows.length === 0 && <p className="compare-no-differences">Các tiêu chí hiện tại không có khác biệt.</p>}
      </div>

      <div className="compare-footer-action"><Link className="button-secondary" to="/#catalog">Thêm sản phẩm khác</Link></div>
    </section>
  )
}
