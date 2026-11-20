import type { ProductRatingSummary } from './reviews.api'
import { RatingStars } from './RatingStars'

interface RatingSummaryProps {
  summary: ProductRatingSummary
  selectedRating?: number | null
  onSelectRating?: (rating: number | null) => void
}

export function RatingSummary({
  summary,
  selectedRating,
  onSelectRating,
}: RatingSummaryProps) {
  const { averageRating, reviewCount, distribution } = summary
  const rows = [
    { stars: 5, count: distribution.five },
    { stars: 4, count: distribution.four },
    { stars: 3, count: distribution.three },
    { stars: 2, count: distribution.two },
    { stars: 1, count: distribution.one },
  ]

  return (
    <div className="review-rating-summary" aria-label="Tổng quan đánh giá">
      <div className="rating-overview-score">
        <span className="big-rating-number">{averageRating > 0 ? averageRating.toFixed(1) : '0.0'}</span>
        <RatingStars value={averageRating} size="lg" />
        <span className="rating-total-reviews">
          {reviewCount > 0 ? `${reviewCount} lượt đánh giá` : 'Chưa có đánh giá'}
        </span>
      </div>

      <div className="rating-distribution-bars" role="group" aria-label="Phân bố đánh giá theo sao">
        {rows.map(({ stars, count }) => {
          const percent = reviewCount > 0 ? Math.round((count / reviewCount) * 100) : 0
          const isActive = selectedRating === stars
          return (
            <button
              key={stars}
              type="button"
              className={`distribution-row-btn ${isActive ? 'active' : ''}`}
              aria-label={`${stars} sao: ${count} đánh giá (${percent}%)`}
              aria-pressed={isActive}
              onClick={() => onSelectRating?.(isActive ? null : stars)}
            >
              <span className="dist-star-label">{stars} ★</span>
              <div className="dist-bar-track" aria-hidden="true">
                <div
                  className="dist-bar-fill"
                  style={{ width: `${percent}%` }}
                />
              </div>
              <span className="dist-count-label">{count}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
}

