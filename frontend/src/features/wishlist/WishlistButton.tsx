import { useState } from 'react'
import type { MouseEvent } from 'react'
import { useWishlist } from './useWishlist'
import './wishlist-button.css'

interface WishlistButtonProps {
  productId: string
  productTitle?: string
  size?: 'sm' | 'md' | 'lg'
  className?: string
  showText?: boolean
}

export function WishlistButton({
  productId,
  productTitle,
  size = 'md',
  className = '',
  showText = false,
}: WishlistButtonProps) {
  const { isSaved, toggleWishlist } = useWishlist()
  const saved = isSaved(productId)
  const [pending, setPending] = useState(false)

  const label = saved
    ? `Bỏ lưu ${productTitle ?? 'sản phẩm'} khỏi danh sách yêu thích`
    : `Lưu ${productTitle ?? 'sản phẩm'} vào danh sách yêu thích`

  const handleClick = async (e: MouseEvent<HTMLButtonElement>) => {
    e.preventDefault()
    e.stopPropagation()
    if (pending) return

    setPending(true)
    try {
      await toggleWishlist(productId)
    } catch {
      // Error handled in context
    } finally {
      setPending(false)
    }
  }

  const iconSize = size === 'sm' ? 18 : size === 'lg' ? 24 : 20

  return (
    <button
      type="button"
      className={`wishlist-btn wishlist-btn-${size} ${saved ? 'is-saved' : ''} ${className}`}
      onClick={handleClick}
      disabled={pending}
      aria-label={label}
      aria-pressed={saved}
      title={label}
      data-testid={`wishlist-btn-${productId}`}
    >
      <svg
        viewBox="0 0 24 24"
        width={iconSize}
        height={iconSize}
        fill={saved ? 'currentColor' : 'none'}
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
        className="wishlist-icon"
      >
        <path d="M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" />
      </svg>
      {showText && (
        <span className="wishlist-btn-text">
          {saved ? 'Đã lưu yêu thích' : 'Lưu yêu thích'}
        </span>
      )}
    </button>
  )
}
