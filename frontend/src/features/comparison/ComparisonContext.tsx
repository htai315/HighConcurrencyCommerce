import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ComparisonContext } from './comparison-context'
import type { ComparisonMutationResult } from './comparison-context'
import {
  COMPARISON_CHANGED_EVENT,
  COMPARISON_STORAGE_KEY,
  MAX_COMPARISON_ITEMS,
  isProductId,
  parseComparisonItems,
  readComparisonItems,
  writeComparisonItems,
} from './comparison-storage'
import type { ComparisonItem } from './comparison-storage'

function sameKnownCategory(items: readonly ComparisonItem[], candidate: ComparisonItem): boolean {
  if (!candidate.categoryId) return true
  return items.every(item => !item.categoryId || item.categoryId === candidate.categoryId)
}

export function ComparisonProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ComparisonItem[]>(readComparisonItems)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const refresh = () => setItems(readComparisonItems())
    const handleStorage = (event: StorageEvent) => {
      if (event.key === COMPARISON_STORAGE_KEY || event.key === null) refresh()
    }
    window.addEventListener(COMPARISON_CHANGED_EVENT, refresh)
    window.addEventListener('storage', handleStorage)
    return () => {
      window.removeEventListener(COMPARISON_CHANGED_EVENT, refresh)
      window.removeEventListener('storage', handleStorage)
    }
  }, [])

  const commit = useCallback((next: ComparisonItem[]) => {
    const safeItems = parseComparisonItems(next)
    setItems(safeItems)
    writeComparisonItems(safeItems)
  }, [])

  const contains = useCallback((productId: string) => items.some(item => item.id === productId.toLowerCase()), [items])
  const canCompareWith = useCallback((item: ComparisonItem) =>
    isProductId(item.id) && (contains(item.id) || (items.length < MAX_COMPARISON_ITEMS && sameKnownCategory(items, item))),
  [contains, items])

  const add = useCallback((item: ComparisonItem): ComparisonMutationResult => {
    const [safeItem] = parseComparisonItems([item])
    if (!safeItem) {
      setNotice('Không thể thêm sản phẩm này vào danh sách so sánh.')
      return { ok: false, reason: 'INVALID_PRODUCT' }
    }
    if (items.some(current => current.id === safeItem.id)) return { ok: true, action: 'ADDED' }
    if (items.length >= MAX_COMPARISON_ITEMS) {
      setNotice(`Bạn chỉ có thể so sánh tối đa ${MAX_COMPARISON_ITEMS} sản phẩm.`)
      return { ok: false, reason: 'LIMIT_REACHED' }
    }
    if (!sameKnownCategory(items, safeItem)) {
      setNotice('Chỉ có thể so sánh các sản phẩm trong cùng một danh mục.')
      return { ok: false, reason: 'CATEGORY_MISMATCH' }
    }
    commit([...items, safeItem])
    setNotice(`Đã thêm ${safeItem.title} vào danh sách so sánh.`)
    return { ok: true, action: 'ADDED' }
  }, [commit, items])

  const remove = useCallback((productId: string): ComparisonMutationResult => {
    const normalizedId = productId.toLowerCase()
    if (!isProductId(normalizedId)) return { ok: false, reason: 'INVALID_PRODUCT' }
    const existing = items.find(item => item.id === normalizedId)
    if (!existing) return { ok: true, action: 'REMOVED' }
    commit(items.filter(item => item.id !== normalizedId))
    setNotice(`Đã bỏ ${existing.title} khỏi danh sách so sánh.`)
    return { ok: true, action: 'REMOVED' }
  }, [commit, items])

  const toggle = useCallback((item: ComparisonItem): ComparisonMutationResult =>
    contains(item.id) ? remove(item.id) : add(item), [add, contains, remove])

  const replace = useCallback((next: ComparisonItem[]) => {
    commit(next)
  }, [commit])

  const clear = useCallback(() => {
    commit([])
    setNotice('Đã xóa danh sách so sánh.')
  }, [commit])

  const value = useMemo(() => ({
    items,
    count: items.length,
    notice,
    contains,
    canCompareWith,
    add,
    remove,
    toggle,
    replace,
    clear,
    clearNotice: () => setNotice(null),
  }), [add, canCompareWith, clear, contains, items, notice, remove, replace, toggle])

  return <ComparisonContext.Provider value={value}>{children}</ComparisonContext.Provider>
}
