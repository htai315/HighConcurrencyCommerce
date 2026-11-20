import type { PublicProductReview } from './reviews.api'
import { ReviewCard } from './ReviewCard'

interface ReviewListProps {
  reviews: PublicProductReview[]
  totalCount: number
  currentPage: number
  pageSize: number
  sort: 'newest' | 'highest' | 'lowest'
  selectedRating: number | null
  loading?: boolean
  onSortChange: (sort: 'newest' | 'highest' | 'lowest') => void
  onPageChange: (page: number) => void
  onRatingFilterChange: (rating: number | null) => void
}

export function ReviewList({
  reviews,
  totalCount,
  currentPage,
  pageSize,
  sort,
  selectedRating,
  loading = false,
  onSortChange,
  onPageChange,
  onRatingFilterChange,
}: ReviewListProps) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))

  return (
    <div className="product-reviews-list-container">
      <div className="reviews-filter-toolbar">
        <div className="rating-filter-pills" role="group" aria-label="Lọc theo số sao">
          <button
            type="button"
            className={`filter-pill ${selectedRating === null ? 'active' : ''}`}
            onClick={() => onRatingFilterChange(null)}
          >
            Tất cả
          </button>
          {[5, 4, 3, 2, 1].map(r => (
            <button
              key={r}
              type="button"
              className={`filter-pill ${selectedRating === r ? 'active' : ''}`}
              onClick={() => onRatingFilterChange(selectedRating === r ? null : r)}
            >
              {r} ★
            </button>
          ))}
        </div>

        <div className="reviews-sort-control">
          <label htmlFor="reviews-sort-select" className="sort-label">Sắp xếp:</label>
          <select
            id="reviews-sort-select"
            className="form-select reviews-sort-select"
            value={sort}
            onChange={e => onSortChange(e.target.value as 'newest' | 'highest' | 'lowest')}
          >
            <option value="newest">Mới nhất</option>
            <option value="highest">Đánh giá cao nhất</option>
            <option value="lowest">Đánh giá thấp nhất</option>
          </select>
        </div>
      </div>

      {loading ? (
        <div className="reviews-loading-placeholder" aria-live="polite">
          <span>Đang tải đánh giá…</span>
        </div>
      ) : reviews.length === 0 ? (
        <div className="reviews-empty-state">
          <p>
            {selectedRating !== null
              ? `Chưa có đánh giá ${selectedRating} sao nào.`
              : 'Chưa có đánh giá nào cho sản phẩm này.'}
          </p>
        </div>
      ) : (
        <div className="reviews-cards-list">
          {reviews.map(review => (
            <ReviewCard key={review.id} review={review} />
          ))}
        </div>
      )}

      {totalPages > 1 && (
        <nav className="reviews-pagination" aria-label="Phân trang đánh giá">
          <button
            type="button"
            className="pagination-btn prev-btn"
            disabled={currentPage <= 1 || loading}
            onClick={() => onPageChange(currentPage - 1)}
          >
            ← Trước
          </button>
          <span className="pagination-current-info">
            Trang {currentPage} / {totalPages}
          </span>
          <button
            type="button"
            className="pagination-btn next-btn"
            disabled={currentPage >= totalPages || loading}
            onClick={() => onPageChange(currentPage + 1)}
          >
            Sau →
          </button>
        </nav>
      )}
    </div>
  )
}

