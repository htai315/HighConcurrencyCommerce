import { useContext } from 'react'
import { WishlistContext } from './wishlist-context'
import type { WishlistContextValue } from './wishlist-context'

export function useWishlist(): WishlistContextValue {
  return useContext(WishlistContext)
}
export type { WishlistContextValue }
