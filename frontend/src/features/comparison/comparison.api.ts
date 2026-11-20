import { redirect } from 'react-router-dom'
import type { LoaderFunctionArgs } from 'react-router-dom'
import { parseProductDetail } from '../catalog/catalog.api'
import type { ProductDetail } from '../catalog/catalog.api'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { MAX_COMPARISON_ITEMS, isProductId } from './comparison-storage'

export type ProductComparison = {
  items: ProductDetail[]
  requestedCount: number
  omittedCount: number
}

export type ComparisonLoaderData = {
  comparison: ProductComparison
  requestedIds: string[]
}

function nonNegativeInteger(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  return value
}

export function parseProductComparison(value: unknown, requestedIds: readonly string[]): ProductComparison {
  if (!isRecord(value) || !Array.isArray(value.items)) throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  const items = value.items.map(parseProductDetail)
  const requestedCount = nonNegativeInteger(value.requestedCount)
  const omittedCount = nonNegativeInteger(value.omittedCount)
  const requestedSet = new Set(requestedIds)
  const itemIds = items.map(item => item.id.toLowerCase())
  if (requestedCount !== requestedIds.length || requestedCount !== items.length + omittedCount) {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  if (new Set(itemIds).size !== itemIds.length || itemIds.some(id => !requestedSet.has(id))) {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  const expectedOrder = requestedIds.filter(id => itemIds.includes(id))
  if (expectedOrder.some((id, index) => itemIds[index] !== id)) {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  const categoryIds = new Set(
    items
      .map(item => item.category?.id.toLowerCase())
      .filter((id): id is string => id !== undefined),
  )
  if (categoryIds.size > 1) {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  return { items, requestedCount, omittedCount }
}

export function parseComparisonIds(url: URL): { ids: string[]; canonical: boolean } {
  const raw = url.searchParams.get('ids')?.split(',').map(value => value.trim().toLowerCase()).filter(Boolean) ?? []
  if (raw.length === 0) return { ids: [], canonical: !url.searchParams.has('ids') || url.searchParams.get('ids') === '' }
  if (raw.length > MAX_COMPARISON_ITEMS || raw.some(id => !isProductId(id))) {
    throw new ApiError(400, { code: 'VALIDATION_ERROR' })
  }
  const ids = [...new Set(raw)]
  return { ids, canonical: ids.length === raw.length && url.searchParams.get('ids') === ids.join(',') }
}

export async function comparisonLoader({ request }: LoaderFunctionArgs): Promise<ComparisonLoaderData> {
  const url = new URL(request.url)
  const { ids, canonical } = parseComparisonIds(url)
  if (!canonical) {
    const next = new URLSearchParams(url.searchParams)
    if (ids.length) next.set('ids', ids.join(','))
    else next.delete('ids')
    throw redirect(`/compare${next.size ? `?${next}` : ''}`)
  }
  if (ids.length === 0) {
    return { comparison: { items: [], requestedCount: 0, omittedCount: 0 }, requestedIds: [] }
  }
  const query = new URLSearchParams()
  ids.forEach(id => query.append('id', id))
  const response = await apiRequest(`/catalog/products/compare?${query}`, { signal: request.signal, cache: 'no-store' })
  return { comparison: parseProductComparison(response, ids), requestedIds: ids }
}
