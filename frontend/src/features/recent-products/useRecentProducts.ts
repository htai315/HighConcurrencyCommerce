import { useContext } from 'react'
import { RecentProductsContext } from './recent-products-context'
import type { RecentProductsContextValue } from './recent-products-context'

export function useRecentProducts(): RecentProductsContextValue {
  const context = useContext(RecentProductsContext)
  if (!context) {
    throw new Error('useRecentProducts must be used within a RecentProductsProvider')
  }
  return context
}

