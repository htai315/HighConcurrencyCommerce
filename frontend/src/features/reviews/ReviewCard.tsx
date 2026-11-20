import type { PublicProductReview, MyProductReview } from './reviews.api'
import { RatingStars } from './RatingStars'

interface ReviewCardProps {
  review: PublicProductReview | MyProductReview
  isOwner?: boolean
  onEdit?: () => void
  onDelete?: () => void
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    if (isNaN(d.getTime())) return iso
    return new Intl.DateTimeFormat('vi-VN', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).format(d)
  } catch {
    return iso
  }
}

export function ReviewCard({
  review,
  isOwner = false,
  onEdit,
  onDelete,
}: ReviewCardProps) {
  const isPublic = 'reviewerDisplayName' in review
  const displayName = isPublic ? review.reviewerDisplayName : 'Bạn (Tác giả)'
  const isVerified = isPublic ? review.verifiedPurchase : true
  const isHidden = 'reviewStatus' in review && review.reviewStatus === 'HIDDEN'
  const isEdited = review.updatedAt !== review.createdAt

  return (
    <article className={`product-review-card ${isHidden ? 'review-card-hidden' : ''}`} data-review-id={review.id}>
      <header className="review-card-header">
        <div className="reviewer-info">
          <div className="reviewer-avatar" aria-hidden="true">
            {displayName.charAt(0).toUpperCase()}
          </div>
          <div>
            <div className="reviewer-name-row">
              <strong className="reviewer-name">{displayName}</strong>
              {isOwner && <span className="owner-badge">Của bạn</span>}
              {isVerified && (
                <span className="verified-badge" title="Khách hàng đã mua và nhận sản phẩm này">
                  ✓ Đã mua hàng tại shop
                </span>
              )}
            </div>
            <div className="review-meta-row">
              <RatingStars value={review.rating} size="sm" />
              <time className="review-timestamp" dateTime={review.createdAt}>
                {formatDate(review.createdAt)}
              </time>
              {isEdited && <span className="edited-indicator">(Đã chỉnh sửa)</span>}
            </div>
          </div>
        </div>

        {isOwner && (
          <div className="review-card-actions">
            {onEdit && (
              <button
                type="button"
                className="button-link edit-review-btn"
                onClick={onEdit}
              >
                Chỉnh sửa
              </button>
            )}
            {onDelete && (
              <button
                type="button"
                className="button-link danger delete-review-btn"
                onClick={onDelete}
              >
                Xóa
              </button>
            )}
          </div>
        )}
      </header>

      {isHidden && (
        <div className="review-hidden-notice" role="alert">
          <span className="notice-icon" aria-hidden="true">⚠️</span>
          <div>
            <strong>Đánh giá này đang bị ẩn bởi quản trị viên.</strong>
            {'moderationReason' in review && review.moderationReason && (
              <p className="moderation-reason">Lý do: {review.moderationReason}</p>
            )}
          </div>
        </div>
      )}

      <div className="review-card-body">
        <p className="review-content-text">{review.content}</p>
      </div>
    </article>
  )
}

