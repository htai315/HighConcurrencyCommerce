export const COMPARISON_STORAGE_KEY = 'hcc.product-comparison.v1'
export const COMPARISON_CHANGED_EVENT = 'hcc:comparison-changed'
export const MAX_COMPARISON_ITEMS = 4

export type ComparisonItem = {
  id: string
  title: string
  imageUrl: string | null
  categoryId: string | null
  categoryName: string | null
}

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

export function isProductId(value: unknown): value is string {
  return typeof value === 'string' && GUID_PATTERN.test(value) && value.toLowerCase() !== EMPTY_GUID
}

function parseItem(value: unknown): ComparisonItem | null {
  if (!value || typeof value !== 'object') return null
  const candidate = value as Record<string, unknown>
  if (!isProductId(candidate.id) || typeof candidate.title !== 'string') return null
  const title = candidate.title.trim()
  if (!title || title.length > 300) return null
  if (candidate.imageUrl !== null && typeof candidate.imageUrl !== 'string') return null
  if (candidate.categoryId !== null && !isProductId(candidate.categoryId)) return null
  if (candidate.categoryName !== null && typeof candidate.categoryName !== 'string') return null
  return {
    id: candidate.id.toLowerCase(),
    title,
    imageUrl: sanitizeComparisonImageUrl(candidate.imageUrl),
    categoryId: candidate.categoryId?.toLowerCase() ?? null,
    categoryName: candidate.categoryName?.trim() || null,
  }
}

export function sanitizeComparisonImageUrl(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const url = value.trim()
  if (!url || url.length > 2048 || url.startsWith('//') || url.includes('\\')) return null
  if (url.startsWith('/')) return url
  if (/^https?:\/\//i.test(url)) return url
  if (/^[a-z][a-z0-9+.-]*:/i.test(url)) return null
  return url
}

export function resolveComparisonImageUrl(value: unknown): string | null {
  const safe = sanitizeComparisonImageUrl(value)
  if (!safe) return null
  return safe.startsWith('/') || /^https?:\/\//i.test(safe) ? safe : `/${safe}`
}

export function parseComparisonItems(value: unknown): ComparisonItem[] {
  if (!Array.isArray(value)) return []
  const result: ComparisonItem[] = []
  const seen = new Set<string>()
  for (const candidate of value) {
    const item = parseItem(candidate)
    if (!item || seen.has(item.id)) continue
    result.push(item)
    seen.add(item.id)
    if (result.length === MAX_COMPARISON_ITEMS) break
  }
  return result
}

export function readComparisonItems(): ComparisonItem[] {
  try {
    return parseComparisonItems(JSON.parse(localStorage.getItem(COMPARISON_STORAGE_KEY) ?? '[]'))
  } catch {
    try { localStorage.removeItem(COMPARISON_STORAGE_KEY) } catch { /* Storage is optional. */ }
    return []
  }
}

export function writeComparisonItems(items: ComparisonItem[]): boolean {
  const safeItems = parseComparisonItems(items)
  try {
    localStorage.setItem(COMPARISON_STORAGE_KEY, JSON.stringify(safeItems))
    window.dispatchEvent(new CustomEvent(COMPARISON_CHANGED_EVENT))
    return true
  } catch {
    return false
  }
}

export function buildComparisonUrl(items: readonly Pick<ComparisonItem, 'id'>[]): string {
  const ids = items.slice(0, MAX_COMPARISON_ITEMS).map(item => item.id).join(',')
  return ids ? `/compare?${new URLSearchParams({ ids })}` : '/compare'
}
