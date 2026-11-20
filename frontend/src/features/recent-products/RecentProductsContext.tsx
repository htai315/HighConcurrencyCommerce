import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { RecentProductsContext } from './recent-products-context'
import {
  RECENT_PRODUCTS_CHANGED_EVENT,
  RECENT_PRODUCTS_STORAGE_KEY,
  clearRecentProducts,
  pruneRecentProducts,
  readRecentProducts,
  recordProductView,
  removeRecentProduct,
} from './recent-products-storage'

export function RecentProductsProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState(() => readRecentProducts())

  useEffect(() => {
    const refresh = () => setItems(readRecentProducts())
    const handleStorage = (event: StorageEvent) => {
      if (event.key === RECENT_PRODUCTS_STORAGE_KEY || event.key === null) refresh()
    }
    window.addEventListener(RECENT_PRODUCTS_CHANGED_EVENT, refresh)
    window.addEventListener('storage', handleStorage)
    return () => {
      window.removeEventListener(RECENT_PRODUCTS_CHANGED_EVENT, refresh)
      window.removeEventListener('storage', handleStorage)
    }
  }, [])

  const recordView = useCallback((productId: string) => {
    const next = recordProductView(productId)
    setItems(next)
  }, [])

  const remove = useCallback((productId: string) => {
    const next = removeRecentProduct(productId)
    setItems(next)
  }, [])

  const clear = useCallback(() => {
    clearRecentProducts()
    setItems([])
  }, [])

  const prune = useCallback((validIds: readonly string[]) => {
    const next = pruneRecentProducts(validIds)
    setItems(next)
  }, [])

  const recentIds = useMemo(() => items.map(item => item.productId), [items])

  const value = useMemo(() => ({
    recentIds,
    recordView,
    remove,
    clear,
    prune,
  }), [recentIds, recordView, remove, clear, prune])

  return (
    <RecentProductsContext.Provider value={value}>
      {children}
    </RecentProductsContext.Provider>
  )
}

