import type { MouseEvent } from 'react'
import { useComparison } from './useComparison'
import type { ComparisonItem } from './comparison-storage'
import './comparison.css'

type CompareButtonProps = {
  product: ComparisonItem
  compact?: boolean
  className?: string
}

export function CompareButton({ product, compact = false, className = '' }: CompareButtonProps) {
  const { contains, toggle } = useComparison()
  const selected = contains(product.id)
  const label = selected ? `Bỏ ${product.title} khỏi so sánh` : `Thêm ${product.title} vào so sánh`

  function handleClick(event: MouseEvent<HTMLButtonElement>) {
    event.preventDefault()
    event.stopPropagation()
    toggle(product)
  }

  return (
    <button
      type="button"
      className={`compare-button${selected ? ' is-selected' : ''}${compact ? ' is-compact' : ''} ${className}`.trim()}
      onClick={handleClick}
      aria-label={label}
      aria-pressed={selected}
      title={label}
      data-testid={`compare-btn-${product.id}`}
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M8 3v18M16 3v18M3 7h10M11 17h10M5 7l-2 4h4L5 7Zm14 10-2 4h4l-2-4Z" />
      </svg>
      {!compact && <span>{selected ? 'Đã chọn so sánh' : 'So sánh'}</span>}
    </button>
  )
}
