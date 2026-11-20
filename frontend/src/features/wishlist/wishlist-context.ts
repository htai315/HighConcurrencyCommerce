import { createContext } from 'react'
import type { UpdateWishlistAlertSettings, WishlistAlertSettings, WishlistAlertSummary } from './wishlist.api'

export interface WishlistContextValue {
  wishlistIds: Set<string>
  totalCount: number
  emailVerified: boolean
  alertSummaries: ReadonlyMap<string, WishlistAlertSummary>
  loading: boolean
  error: string | null
  isSaved: (productId: string) => boolean
  getAlertSummary: (productId: string) => WishlistAlertSummary | undefined
  loadAlertSettings: (productId: string, signal?: AbortSignal) => Promise<WishlistAlertSettings>
  saveAlertSettings: (productId: string, request: UpdateWishlistAlertSettings, signal?: AbortSignal) => Promise<WishlistAlertSettings>
  toggleWishlist: (productId: string) => Promise<boolean>
  refreshSummary: () => Promise<void>
  clearError: () => void
}

export const defaultWishlistContext: WishlistContextValue = {
  wishlistIds: new Set(),
  totalCount: 0,
  emailVerified: false,
  alertSummaries: new Map(),
  loading: false,
  error: null,
  isSaved: () => false,
  getAlertSummary: () => undefined,
  loadAlertSettings: async () => { throw new Error('WishlistProvider is missing.') },
  saveAlertSettings: async () => { throw new Error('WishlistProvider is missing.') },
  toggleWishlist: async () => false,
  refreshSummary: async () => {},
  clearError: () => {},
}

export const WishlistContext = createContext<WishlistContextValue>(defaultWishlistContext)

