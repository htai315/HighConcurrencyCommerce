import { useState } from 'react'
import { RatingStars } from './RatingStars'

interface ReviewFormProps {
  initialRating?: number
  initialContent?: string
  isEditing?: boolean
  submitting?: boolean
  errorMessage?: string | null
  onSubmit: (data: { rating: number; content: string }) => Promise<void>
  onCancel?: () => void
}

export function ReviewForm({
  initialRating = 5,
  initialContent = '',
  isEditing = false,
  submitting = false,
  errorMessage = null,
  onSubmit,
  onCancel,
}: ReviewFormProps) {
  const [rating, setRating] = useState(initialRating)
  const [content, setContent] = useState(initialContent)
  const [clientError, setClientError] = useState<string | null>(null)

  const trimmedLength = content.trim().length
  const isValid = rating >= 1 && rating <= 5 && trimmedLength >= 10 && trimmedLength <= 2000

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (rating < 1 || rating > 5) {
      setClientError('Vui lòng chọn số sao đánh giá (1–5 sao).')
      return
    }
    if (trimmedLength < 10) {
      setClientError('Nội dung đánh giá cần ít nhất 10 ký tự.')
      return
    }
    if (trimmedLength > 2000) {
      setClientError('Nội dung đánh giá không được vượt quá 2.000 ký tự.')
      return
    }
    setClientError(null)
    await onSubmit({ rating, content: content.trim() })
  }

  const activeError = clientError ?? errorMessage

  return (
    <form className="product-review-form" onSubmit={handleSubmit} noValidate>
      <h3>{isEditing ? 'Chỉnh sửa đánh giá của bạn' : 'Viết đánh giá sản phẩm'}</h3>

      {activeError && (
        <div className="alert alert-danger review-form-error" role="alert">
          <span>{activeError}</span>
        </div>
      )}

      <div className="form-group rating-selection-group">
        <span className="form-label" id="rating-label">
          Đánh giá chung <span className="required-mark">*</span>
        </span>
        <div aria-labelledby="rating-label">
          <RatingStars
            value={rating}
            interactive
            onChange={setRating}
            size="lg"
          />
        </div>
        <span className="rating-hint-text">
          {rating === 5 && 'Tuyệt vời'}
          {rating === 4 && 'Hài lòng'}
          {rating === 3 && 'Bình thường'}
          {rating === 2 && 'Không hài lòng'}
          {rating === 1 && 'Rất tệ'}
        </span>
      </div>

      <div className="form-group review-textarea-group">
        <label htmlFor="review-content" className="form-label">
          Nội dung nhận xét <span className="required-mark">*</span>
        </label>
        <textarea
          id="review-content"
          name="reviewContent"
          className="form-textarea review-content-input"
          rows={4}
          maxLength={2000}
          placeholder="Hãy chia sẻ trải nghiệm thực tế về chất lượng, đóng gói và dịch vụ để giúp người mua khác..."
          value={content}
          onChange={e => {
            setContent(e.target.value)
            if (clientError) setClientError(null)
          }}
          disabled={submitting}
        />
        <div className="textarea-footer">
          <span className={`char-counter ${trimmedLength < 10 ? 'insufficient' : ''}`}>
            {trimmedLength < 10
              ? `Tối thiểu 10 ký tự (còn thiếu ${10 - trimmedLength})`
              : `${trimmedLength} / 2.000 ký tự`}
          </span>
        </div>
      </div>

      <div className="review-form-actions">
        {onCancel && (
          <button
            type="button"
            className="button secondary cancel-review-btn"
            onClick={onCancel}
            disabled={submitting}
          >
            Hủy bỏ
          </button>
        )}
        <button
          type="submit"
          className="button primary submit-review-btn"
          disabled={!isValid || submitting}
        >
          {submitting
            ? isEditing ? 'Đang lưu…' : 'Đang gửi…'
            : isEditing ? 'Cập nhật đánh giá' : 'Gửi đánh giá'}
        </button>
      </div>
    </form>
  )
}

