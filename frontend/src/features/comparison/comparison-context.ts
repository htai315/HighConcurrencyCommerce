import { createContext } from 'react'
import type { ComparisonItem } from './comparison-storage'

export type ComparisonFailureReason = 'INVALID_PRODUCT' | 'LIMIT_REACHED' | 'CATEGORY_MISMATCH'
export type ComparisonMutationResult =
  | { ok: true; action: 'ADDED' | 'REMOVED' }
  | { ok: false; reason: ComparisonFailureReason }

export interface ComparisonContextValue {
  items: ComparisonItem[]
  count: number
  notice: string | null
  contains: (productId: string) => boolean
  canCompareWith: (item: ComparisonItem) => boolean
  add: (item: ComparisonItem) => ComparisonMutationResult
  remove: (productId: string) => ComparisonMutationResult
  toggle: (item: ComparisonItem) => ComparisonMutationResult
  replace: (items: ComparisonItem[]) => void
  clear: () => void
  clearNotice: () => void
}

const missing = (): ComparisonMutationResult => ({ ok: false, reason: 'INVALID_PRODUCT' })

export const ComparisonContext = createContext<ComparisonContextValue>({
  items: [],
  count: 0,
  notice: null,
  contains: () => false,
  canCompareWith: () => false,
  add: missing,
  remove: missing,
  toggle: missing,
  replace: () => {},
  clear: () => {},
  clearNotice: () => {},
})
