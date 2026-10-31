import { Form, Link, useLoaderData, useNavigation, useSearchParams } from 'react-router-dom'
import { catalogLoader } from '../features/catalog/catalog.api'
import { ProductCard } from '../features/catalog/ProductCard'
import { FlashSaleShowcase } from '../features/flash-sale/FlashSaleShowcase'
import heroImage from '../assets/badminton-hero-v2.png'

export function StorefrontPage() {
  const { categories, products } = useLoaderData<typeof catalogLoader>()
  const [query] = useSearchParams()
  const busy = useNavigation().state !== 'idle'
  const pages = Math.ceil(products.totalCount / products.pageSize)
  const filtered = ['q', 'categoryId', 'minPrice', 'maxPrice'].some(key => Boolean(query.get(key)))

  function categoryLink(id: string) {
    const next = new URLSearchParams(query)
    if (id) next.set('categoryId', id)
    else next.delete('categoryId')
    next.set('page', '1')
    return `/?${next}#catalog`
  }

  function pageLink(page: number) {
    const next = new URLSearchParams(query)
    next.set('page', String(page))
    return `/?${next}`
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
        <Form key={query.toString()} method="get" className="catalog-filters" role="search">
          <label className="search-field">
            Tìm sản phẩm
            <input
              type="search"
              name="q"
              maxLength={100}
              placeholder="Bạn đang tìm vợt, giày hay áo cầu lông?"
              defaultValue={query.get('q') ?? ''}
            />
          </label>
          <label>
            Danh mục
            <select name="categoryId" defaultValue={query.get('categoryId') ?? ''}>
              <option value="">Tất cả danh mục</option>
              {categories.map(c => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
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
              defaultValue={query.get('maxPrice') ?? ''}
            />
          </label>
          <label>
            Sắp xếp
            <select name="sort" defaultValue={query.get('sort') ?? 'newest'}>
              <option value="newest">Mới nhất</option>
              <option value="price-asc">Giá tăng dần</option>
              <option value="price-desc">Giá giảm dần</option>
            </select>
          </label>
          <input type="hidden" name="page" value="1" />
          <button className="button-primary" disabled={busy} type="submit">
            Áp dụng
          </button>
          <Link className="button-secondary" to="/">
            Xóa bộ lọc
          </Link>
        </Form>

        <p role="status" className="catalog-result-count">
          {busy ? 'Đang tải sản phẩm…' : `${products.totalCount} sản phẩm`}
        </p>

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
      </section>
    </>
  )
}
