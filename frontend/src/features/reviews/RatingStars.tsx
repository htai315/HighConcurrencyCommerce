interface RatingStarsProps {
  value: number
  max?: number
  interactive?: boolean
  onChange?: (rating: number) => void
  size?: 'sm' | 'md' | 'lg'
}

export function RatingStars({
  value,
  max = 5,
  interactive = false,
  onChange,
  size = 'md',
}: RatingStarsProps) {
  if (interactive) {
    return (
      <div
        className={`rating-stars-input rating-stars-${size}`}
        role="radiogroup"
        aria-label="Chọn số sao đánh giá"
      >
        {Array.from({ length: max }, (_, i) => {
          const star = i + 1
          const isSelected = value === star
          const isFilled = star <= value
          return (
            <button
              key={star}
              type="button"
              role="radio"
              aria-checked={isSelected}
              aria-label={`${star} trên ${max} sao`}
              className={`star-button ${isFilled ? 'filled' : 'empty'}`}
              onClick={() => onChange?.(star)}
            >
              ★
            </button>
          )
        })}
      </div>
    )
  }

  return (
    <div
      className={`rating-stars-display rating-stars-${size}`}
      aria-label={`${value.toFixed(1)} trên ${max} sao`}
    >
      {Array.from({ length: max }, (_, i) => {
        const star = i + 1
        const isFilled = star <= Math.round(value)
        return (
          <span
            key={star}
            aria-hidden="true"
            className={`star-icon ${isFilled ? 'filled' : 'empty'}`}
          >
            ★
          </span>
        )
      })}
    </div>
  )
}

