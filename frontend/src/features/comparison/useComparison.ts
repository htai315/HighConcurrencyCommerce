import { useContext } from 'react'
import { ComparisonContext } from './comparison-context'
import type { ComparisonContextValue } from './comparison-context'

export function useComparison(): ComparisonContextValue {
  return useContext(ComparisonContext)
}

export type { ComparisonContextValue }
