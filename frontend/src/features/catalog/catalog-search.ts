const CATALOG_QUERY_KEYS = [
  'q',
  'categoryId',
  'brandId',
  'minPrice',
  'maxPrice',
  'inStock',
  'minRating',
  'sort',
  'page',
  'pageSize',
] as const

const HISTORY_KEY = 'hcc:catalog-search-history:v1'
const MAX_HISTORY_ITEMS = 5

function normalizedHistoryKey(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd')
    .replace(/Đ/g, 'D')
    .trim()
    .replace(/\s+/g, ' ')
    .toLocaleLowerCase('vi')
}

export function buildCatalogUrl(source: URLSearchParams, includeFragment = true): string {
  const canonical = new URLSearchParams()
  const hasQuery = Boolean(source.get('q')?.trim())
  for (const key of CATALOG_QUERY_KEYS) {
    const value = source.get(key)?.trim()
    if (!value) continue
    if (key === 'page' && value === '1') continue
    if (key === 'inStock' && value !== 'true') continue
    if (key === 'sort' && ((value === 'newest' && !hasQuery) || (value === 'relevance' && hasQuery))) continue
    if (key === 'sort' && value === 'relevance' && !hasQuery) continue
    canonical.set(key, value)
  }
  const search = canonical.toString()
  return `/${search ? `?${search}` : ''}${includeFragment ? '#catalog' : ''}`
}

export function buildCatalogSearchUrl(current: URLSearchParams, query: string, includeFragment = true): string {
  const next = new URLSearchParams(current)
  const trimmed = query.trim().replace(/\s+/g, ' ')
  if (trimmed) next.set('q', trimmed)
  else next.delete('q')
  next.delete('page')
  return buildCatalogUrl(next, includeFragment)
}

export function readCatalogSearchHistory(): string[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(HISTORY_KEY) ?? '[]')
    if (!Array.isArray(value)) return []
    return value.filter((item): item is string => typeof item === 'string' && Boolean(item.trim())).slice(0, MAX_HISTORY_ITEMS)
  } catch {
    return []
  }
}

export function rememberCatalogSearch(query: string): string[] {
  const displayValue = query.trim().replace(/\s+/g, ' ')
  if (!displayValue) return readCatalogSearchHistory()
  const key = normalizedHistoryKey(displayValue)
  const next = [displayValue, ...readCatalogSearchHistory().filter(item => normalizedHistoryKey(item) !== key)].slice(0, MAX_HISTORY_ITEMS)
  try { localStorage.setItem(HISTORY_KEY, JSON.stringify(next)) } catch { /* Private/storage-blocked mode keeps search functional. */ }
  return next
}

export function removeCatalogSearchHistory(query: string): string[] {
  const key = normalizedHistoryKey(query)
  const next = readCatalogSearchHistory().filter(item => normalizedHistoryKey(item) !== key)
  try { localStorage.setItem(HISTORY_KEY, JSON.stringify(next)) } catch { /* Keep the in-memory result. */ }
  return next
}

export function clearCatalogSearchHistory(): void {
  try { localStorage.removeItem(HISTORY_KEY) } catch { /* Storage is optional. */ }
}
