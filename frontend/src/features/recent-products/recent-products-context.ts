import { createContext } from 'react'

export type RecentProductsContextValue = {
  recentIds: string[]
  recordView: (productId: string) => void
  remove: (productId: string) => void
  clear: () => void
  prune: (validIds: readonly string[]) => void
}

export const RecentProductsContext = createContext<RecentProductsContextValue | null>(null)

