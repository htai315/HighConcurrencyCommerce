import { Form, Link, useLoaderData, useNavigate, useNavigation, useSearchParams } from 'react-router-dom'
import { catalogLoader, formatPrice } from '../features/catalog/catalog.api'
import { CatalogSearchBox } from '../features/catalog/CatalogSearchBox'
import { buildCatalogUrl } from '../features/catalog/catalog-search'
import { ProductCard } from '../features/catalog/ProductCard'
import { FlashSaleShowcase } from '../features/flash-sale/FlashSaleShowcase'
import { RecentlyViewedSection } from '../features/recent-products/RecentlyViewedSection'
import heroImage from '../assets/badminton-hero-v2.webp'
import { useLayoutEffect, useRef, useState } from 'react'
import '../components/layout/catalog-showroom.css'

export function StorefrontPage() {
  const { categories, brands, products } = useLoaderData<typeof catalogLoader>()
  const [query] = useSearchParams()
  const navigate = useNavigate()
  const busy = useNavigation().state !== 'idle'
  const [filtersOpen, setFiltersOpen] = useState(false)
  const filterToggleRef = useRef<HTMLButtonElement>(null)
  const filterFormRef = useRef<HTMLFormElement>(null)
  const queryKey = query.toString()
  const pages = Math.ceil(products.totalCount / products.pageSize)
  const filterKeys = ['q', 'categoryId', 'brandId', 'minPrice', 'maxPrice', 'inStock', 'minRating'] as const
  const activeFilterKeys = filterKeys.filter(key => key === 'inStock' ? query.get(key) === 'true' : Boolean(query.get(key)))
  const filtered = activeFilterKeys.length > 0
  const categoryCounts = new Map(products.facets?.categories.map(item => [item.id, item.count]) ?? [])
  const brandCounts = new Map(products.facets?.brands.map(item => [item.id, item.count]) ?? [])

  useLayoutEffect(() => {
    if (!filtersOpen) return
    const form = filterFormRef.current
    if (!form) return
    const activeForm = form
    const trigger = filterToggleRef.current
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const focusableSelector = 'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'
    // Wait until the pointer/click default focus has settled; otherwise the
    // trigger can steal focus back after React commits the open drawer.
    const focusTimer = window.setTimeout(() => {
      activeForm.querySelector<HTMLElement>('[data-filter-close]')?.focus({ preventScroll: true })
    }, 50)
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.preventDefault()
        setFiltersOpen(false)
        return
      }
      if (event.key !== 'Tab') return
      const focusable = Array.from(activeForm.querySelectorAll<HTMLElement>(focusableSelector)).filter(element => !element.hidden)
      const first = focusable[0]
      const last = focusable.at(-1)
      if (!first || !last) return
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
      window.clearTimeout(focusTimer)
      document.removeEventListener('keydown', handleKeyDown)
      document.body.style.overflow = previousOverflow
      window.setTimeout(() => trigger?.focus({ preventScroll: true }), 0)
    }
  }, [filtersOpen, queryKey])

  function categoryLink(id: string) {
    const next = new URLSearchParams(query)
    if (id) next.set('categoryId', id)
    else next.delete('categoryId')
    next.delete('page')
    return buildCatalogUrl(next)
  }

  function pageLink(page: number) {
    const next = new URLSearchParams(query)
    next.set('page', String(page))
    return buildCatalogUrl(next)
  }

  function submitFilters(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const next = new URLSearchParams()
    for (const key of ['q', 'categoryId', 'brandId', 'minPrice', 'maxPrice', 'inStock', 'minRating', 'sort', 'pageSize']) {
      const value = form.get(key)
      if (typeof value === 'string' && value.trim()) next.set(key, value.trim())
    }
    next.set('page', '1')
    setFiltersOpen(false)
    void navigate(buildCatalogUrl(next))
  }

  function chipLabel(key: typeof filterKeys[number], value: string) {
    if (key === 'categoryId') return categories.find(item => item.id === value)?.name ?? 'Danh mục'
    if (key === 'brandId') return brands.find(item => item.id === value)?.name ?? 'Thương hiệu'
    if (key === 'minPrice') return `Giá từ ${formatPrice(Number(value))}`
    if (key === 'maxPrice') return `Giá đến ${formatPrice(Number(value))}`
    if (key === 'inStock') return 'Chỉ còn hàng'
    if (key === 'minRating') return `Từ ${value}★`
    return `“${value}”`
  }

  return (
    <>
      <section className="hero-athletic" aria-label="Giới thiệu HighCommerce">
        <img className="hero-athletic-image" src={heroImage} alt="Vận động viên cầu lông thực hiện cú đập cầu trên sân thi đấu" />
        <div className="hero-shade" aria-hidden="true" />
        <div className="hero-content">
          <span className="hero-tag">Dành cho đam mê cầu lông</span>
          <h1>
            Chọn đúng gear.<br/>Chơi hết mình.
          </h1>
          <p>
            Từ cây vợt vừa tay đến đôi giày vững bước. Khám phá trang bị phù hợp cho mỗi lần bạn ra sân.
          </p>
          <div className="hero-actions">
            <a href="#catalog" className="hero-cta">Khám phá sản phẩm <span aria-hidden="true">→</span></a>
            <span className="hero-proof">Vợt · Giày · Trang phục · Phụ kiện</span>
          </div>
          <div className="hero-highlights">
            <div className="hero-feature-item">
              <div>
                <strong>Vợt hợp lối chơi</strong>
                <div>Sẵn sàng cho từng pha cầu</div>
              </div>
            </div>
            <div className="hero-feature-item">
              <div>
                <strong>Di chuyển tự tin</strong>
                <div>Giày & trang phục ra sân</div>
              </div>
            </div>
            <div className="hero-feature-item">
              <div>
                <strong>Đủ đồ cho buổi tập</strong>
                <div>Cầu & phụ kiện đồng hành</div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Catalog & Flash Sale Section */}
      <section className="catalog-section" id="catalog" aria-busy={busy}>
        {/* Flash Sale Showcase Widget */}
        <FlashSaleShowcase />

        <div className="section-heading catalog-heading">
          <div>
            <h2>Trang bị cho trận cầu tiếp theo</h2>
            <p>Khám phá vợt, trang phục, cầu và phụ kiện phù hợp với nhịp độ của bạn.</p>
          </div>
        </div>

        {categories.length > 0 && <nav className="category-shortcuts" aria-label="Chọn danh mục sản phẩm">
          <Link to={categoryLink('')} aria-current={!query.get('categoryId') ? 'page' : undefined}>Tất cả sản phẩm</Link>
          {categories.map(category => <Link key={category.id} to={categoryLink(category.id)} aria-current={query.get('categoryId') === category.id ? 'page' : undefined}>{category.name}</Link>)}
        </nav>}
        <button ref={filterToggleRef} type="button" className="button-secondary mobile-filter-toggle" aria-expanded={filtersOpen} aria-controls="catalog-filter-form" onClick={() => setFiltersOpen(true)}>Bộ lọc {filtered ? '• Đang áp dụng' : ''}</button>
        {filtered && <div className="filter-chips" aria-label="Điều kiện đang áp dụng">{activeFilterKeys.map(key => {
          const next = new URLSearchParams(query)
          const rawValue = query.get(key) ?? ''
          next.delete(key)
          next.delete('page')
          const label = chipLabel(key, rawValue)
          return <Link key={key} to={buildCatalogUrl(next)} aria-label={`Bỏ lọc ${label}`}>{label} <span aria-hidden="true">×</span></Link>
        })}<Link className="clear-filter-chip" to="/#catalog">Xóa tất cả</Link></div>}
        {filtersOpen && <button type="button" className="catalog-filter-backdrop" aria-label="Đóng bộ lọc" onClick={() => setFiltersOpen(false)} />}
        <div className={`catalog-workspace${filtersOpen ? ' filters-open' : ''}`}>
        <Form ref={filterFormRef} id="catalog-filter-form" key={queryKey} method="get" className="catalog-filters" role="search" aria-label="Bộ lọc danh mục sản phẩm" onSubmit={submitFilters}>
          <div className="filter-heading">
            <div className="filter-heading-title">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
              </svg>
              <h3>Bộ lọc sản phẩm</h3>
              <button data-filter-close type="button" className="filter-drawer-close" aria-label="Đóng bộ lọc" onClick={() => setFiltersOpen(false)}>×</button>
            </div>
            <p>Tùy chọn phân khúc và tiêu chí trang bị.</p>
          </div>
          <div className="search-field">
            <span>Từ khóa</span>
            <CatalogSearchBox ariaLabel="Tìm sản phẩm" fieldMode placeholder="Tên vợt, mã áo, giày…" />
          </div>
          <label>
            Danh mục
            <select name="categoryId" defaultValue={query.get('categoryId') ?? ''}>
              <option value="">Tất cả danh mục</option>
              {categories.map(c => (
                <option key={c.id} value={c.id}>
                  {c.name} ({categoryCounts.get(c.id) ?? 0})
                </option>
              ))}
            </select>
          </label>
          <label>
            Thương hiệu
            <select name="brandId" defaultValue={query.get('brandId') ?? ''}>
              <option value="">Tất cả thương hiệu</option>
              {brands.map(brand => <option key={brand.id} value={brand.id}>{brand.name} ({brandCounts.get(brand.id) ?? 0})</option>)}
            </select>
          </label>
          <label>
            Giá từ (₫)
            <input
              name="minPrice"
              type="number"
              min="0"
              step="1"
              placeholder="0"
              aria-describedby="catalog-price-range"
              defaultValue={query.get('minPrice') ?? ''}
            />
          </label>
          <label>
            Giá đến (₫)
            <input
              name="maxPrice"
              type="number"
              min="0"
              step="1"
              placeholder="Tối đa"
              aria-describedby="catalog-price-range"
              defaultValue={query.get('maxPrice') ?? ''}
            />
          </label>
          {products.facets && <p id="catalog-price-range" className="filter-hint">Khoảng hiện có: {products.facets.priceMin == null ? '—' : formatPrice(products.facets.priceMin)} – {products.facets.priceMax == null ? '—' : formatPrice(products.facets.priceMax)}</p>}
          <label className="filter-check">
            <input name="inStock" type="checkbox" value="true" defaultChecked={query.get('inStock') === 'true'} />
            <span>Chỉ còn hàng <small>({products.facets?.inStockCount ?? 0})</small></span>
          </label>
          <label>
            Đánh giá
            <select name="minRating" defaultValue={query.get('minRating') ?? ''}>
              <option value="">Tất cả đánh giá</option>
              <option value="4">Từ 4★ trở lên</option>
              <option value="3">Từ 3★ trở lên</option>
              <option value="2">Từ 2★ trở lên</option>
              <option value="1">Từ 1★ trở lên</option>
            </select>
          </label>
          <label>
            Sắp xếp
            <select name="sort" defaultValue={query.get('sort') ?? products.query?.effectiveSort ?? 'newest'}>
              <option value="relevance" disabled={!query.get('q')}>Liên quan nhất</option>
              <option value="newest">Mới nhất</option>
              <option value="price-asc">Giá tăng dần</option>
              <option value="price-desc">Giá giảm dần</option>
              <option value="rating-desc">Đánh giá cao nhất</option>
              <option value="best-selling">Bán chạy nhất</option>
            </select>
          </label>
          <input type="hidden" name="page" value="1" />
          <div className="catalog-filter-actions">
            <button className="button-primary" disabled={busy} type="submit">Áp dụng</button>
            <Link className="button-secondary" to="/#catalog" onClick={() => setFiltersOpen(false)}>Xóa bộ lọc</Link>
          </div>
        </Form>

        <div className="catalog-results">
        <div className="catalog-results-heading">
          <div>
            {query.get('q') ? (
              <h3>Kết quả cho: <em>"{query.get('q')}"</em></h3>
            ) : (
              <h3>{categories.find(category => category.id === query.get('categoryId'))?.name ?? 'Tất cả sản phẩm'}</h3>
            )}
          </div>
          <p role="status" className="catalog-result-count">
            {busy ? 'Đang tải sản phẩm…' : `${products.totalCount} sản phẩm`}
          </p>
        </div>

        {products.items.length ? (
          <div className="product-grid">
            {products.items.map(product => (
              <ProductCard key={product.id} product={product} />
            ))}
          </div>
        ) : (
          <div className="empty-state">
            <div className="empty-state-icon" aria-hidden="true"><svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5"><circle cx="10" cy="10" r="6"/><path d="m15 15 6 6"/></svg></div>
            <h2>{filtered ? 'Chưa tìm thấy sản phẩm phù hợp.' : 'Bộ sưu tập đang được cập nhật'}</h2>
            <p>{filtered ? 'Thử từ khóa khác hoặc mở rộng khoảng giá để tìm thêm lựa chọn.' : 'Các sản phẩm sẽ xuất hiện tại đây khi được cửa hàng công bố. Bạn hãy ghé lại sau nhé.'}</p>
            {filtered &&
            <Link to="/" className="button-secondary">
              Xem tất cả sản phẩm
            </Link>}
          </div>
        )}

        <nav className="catalog-pagination" aria-label="Phân trang sản phẩm">
          {products.page > 1 && (
            <Link className="button-secondary" to={pageLink(products.page - 1)}>
              Trang trước
            </Link>
          )}
          {pages > 0 && products.page <= pages && (
            <span>
              Trang {products.page} / {pages}
            </span>
          )}
          {products.page > pages && products.page > 1 && (
            <Link className="button-secondary" to={pageLink(1)}>
              Về trang đầu
            </Link>
          )}
          {products.page < pages && (
            <Link className="button-secondary" to={pageLink(products.page + 1)}>
              Trang sau
            </Link>
          )}
        </nav>
        </div>
        </div>
      </section>

      <RecentlyViewedSection />
    </>
  )
}
