export const RECENT_PRODUCTS_STORAGE_KEY = 'hcc.recent-products.v1'
export const RECENT_PRODUCTS_CHANGED_EVENT = 'hcc:recent-products-changed'
export const MAX_RECENT_PRODUCTS = 12
export const RECENT_TTL_MS = 30 * 24 * 60 * 60 * 1000 // 30 days
export const MAX_FUTURE_TOLERANCE_MS = 5 * 60 * 1000 // 5 minutes

export type StoredRecentProduct = {
  productId: string
  viewedAt: number
}

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

export function isGuid(value: unknown): value is string {
  return typeof value === 'string' && GUID_PATTERN.test(value) && value.toLowerCase() !== EMPTY_GUID
}

export const isProductId = isGuid

export function parseRecentProducts(raw: unknown, now = Date.now()): StoredRecentProduct[] {
  let data = raw
  if (typeof raw === 'string') {
    try {
      data = JSON.parse(raw)
    } catch {
      return []
    }
  }

  if (!Array.isArray(data)) return []

  const minValidTime = now - RECENT_TTL_MS

  // Map to hold highest viewedAt per productId
  const byId = new Map<string, number>()

  for (const item of data) {
    if (!item || typeof item !== 'object') continue
    const candidate = item as Record<string, unknown>
    if (!isGuid(candidate.productId)) continue
    if (typeof candidate.viewedAt !== 'number' || !Number.isFinite(candidate.viewedAt) || candidate.viewedAt <= 0) continue

    let viewedAt = Math.floor(candidate.viewedAt)
    if (viewedAt < minValidTime) continue
    if (viewedAt > now + MAX_FUTURE_TOLERANCE_MS) continue
    if (viewedAt > now) {
      viewedAt = now
    }

    const normalizedId = candidate.productId.toLowerCase()
    const existing = byId.get(normalizedId)
    if (existing === undefined || viewedAt > existing) {
      byId.set(normalizedId, viewedAt)
    }
  }

  return Array.from(byId.entries())
    .map(([productId, viewedAt]) => ({ productId, viewedAt }))
    .sort((a, b) => b.viewedAt - a.viewedAt)
    .slice(0, MAX_RECENT_PRODUCTS)
}

let inMemoryFallback: StoredRecentProduct[] = []

export function readRecentProducts(now = Date.now()): StoredRecentProduct[] {
  try {
    const raw = localStorage.getItem(RECENT_PRODUCTS_STORAGE_KEY)
    if (raw === null) {
      // A successful null read means the key was removed (possibly by another
      // tab). The memory fallback is only for storage access failures.
      inMemoryFallback = []
      return []
    }

    let decoded: unknown
    try {
      decoded = JSON.parse(raw)
    } catch {
      localStorage.removeItem(RECENT_PRODUCTS_STORAGE_KEY)
      inMemoryFallback = []
      return []
    }
    if (!Array.isArray(decoded)) {
      localStorage.removeItem(RECENT_PRODUCTS_STORAGE_KEY)
      inMemoryFallback = []
      return []
    }

    const parsed = parseRecentProducts(decoded, now)
    inMemoryFallback = parsed
    return parsed
  } catch {
    try {
      localStorage.removeItem(RECENT_PRODUCTS_STORAGE_KEY)
    } catch {
      // Storage unavailable or disabled
    }
    return inMemoryFallback
  }
}

export function writeRecentProducts(items: StoredRecentProduct[], now = Date.now()): boolean {
  const safeItems = parseRecentProducts(items, now)
  inMemoryFallback = safeItems
  try {
    localStorage.setItem(RECENT_PRODUCTS_STORAGE_KEY, JSON.stringify(safeItems))
    window.dispatchEvent(new CustomEvent(RECENT_PRODUCTS_CHANGED_EVENT))
    return true
  } catch {
    window.dispatchEvent(new CustomEvent(RECENT_PRODUCTS_CHANGED_EVENT))
    return false
  }
}

export function recordProductView(productId: string, now = Date.now()): StoredRecentProduct[] {
  if (!isGuid(productId)) {
    return readRecentProducts(now)
  }

  const current = readRecentProducts(now)
  const normalizedId = productId.toLowerCase()
  const filtered = current.filter(item => item.productId.toLowerCase() !== normalizedId)
  const next: StoredRecentProduct[] = [
    { productId: normalizedId, viewedAt: now },
    ...filtered,
  ].slice(0, MAX_RECENT_PRODUCTS)

  writeRecentProducts(next, now)
  return next
}

export function removeRecentProduct(productId: string, now = Date.now()): StoredRecentProduct[] {
  if (!isGuid(productId)) {
    return readRecentProducts(now)
  }

  const current = readRecentProducts(now)
  const normalizedId = productId.toLowerCase()
  const next = current.filter(item => item.productId.toLowerCase() !== normalizedId)
  writeRecentProducts(next, now)
  return next
}

export function clearRecentProducts(): void {
  inMemoryFallback = []
  try {
    localStorage.removeItem(RECENT_PRODUCTS_STORAGE_KEY)
    window.dispatchEvent(new CustomEvent(RECENT_PRODUCTS_CHANGED_EVENT))
  } catch {
    window.dispatchEvent(new CustomEvent(RECENT_PRODUCTS_CHANGED_EVENT))
  }
}

export function pruneRecentProducts(validIds: readonly string[], now = Date.now()): StoredRecentProduct[] {
  const validSet = new Set(validIds.map(id => id.toLowerCase()))
  const current = readRecentProducts(now)
  const next = current.filter(item => validSet.has(item.productId.toLowerCase()))
  if (next.length !== current.length) {
    writeRecentProducts(next, now)
  }
  return next
}
