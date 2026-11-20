const STORAGE_KEY = 'hcc.guest-cart.v1'
const NOTICE_KEY = 'hcc.cart-merge-notice.v1'
const PENDING_MERGE_KEY = 'hcc.pending-cart-merge.v1'

export type GuestCartItem = {
  productSkuId: string
  productId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  unitPrice: number
  imageUrl: string | null
  flashSaleOfferId?: string | null
  quantity: number
}

export type PendingMergeSnapshot = {
  batchId: string
  items: GuestCartItem[]
  createdAt: number
}

function validItem(value: unknown): value is GuestCartItem {
  if (!value || typeof value !== 'object') return false
  const item = value as Record<string, unknown>
  return typeof item.productSkuId === 'string' && typeof item.productId === 'string' &&
    typeof item.productTitle === 'string' && typeof item.skuCode === 'string' &&
    typeof item.optionLabel === 'string' && typeof item.unitPrice === 'number' &&
    Number.isFinite(item.unitPrice) && item.unitPrice >= 0 &&
    (item.imageUrl === null || typeof item.imageUrl === 'string') &&
    (item.flashSaleOfferId === undefined || item.flashSaleOfferId === null || typeof item.flashSaleOfferId === 'string') &&
    Number.isSafeInteger(item.quantity) && Number(item.quantity) >= 1 && Number(item.quantity) <= 99
}

export function readGuestCart(): GuestCartItem[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]')
    return Array.isArray(parsed) ? parsed.filter(validItem) : []
  } catch { return [] }
}

function write(items: GuestCartItem[]) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(items))
  window.dispatchEvent(new Event('hcc:cart-changed'))
}

export function addGuestItem(item: Omit<GuestCartItem, 'quantity'>, quantity = 1): GuestCartItem[] {
  const items = readGuestCart()
  const existing = items.find(entry => entry.productSkuId === item.productSkuId)
  if (existing) {
    existing.quantity = Math.min(99, existing.quantity + quantity)
    existing.flashSaleOfferId = item.flashSaleOfferId ?? null
    existing.unitPrice = item.unitPrice
    existing.productTitle = item.productTitle
    existing.skuCode = item.skuCode
    existing.optionLabel = item.optionLabel
    existing.imageUrl = item.imageUrl
  } else {
    items.push({ ...item, quantity: Math.min(99, Math.max(1, quantity)) })
  }
  write(items)
  return items
}

export function setGuestItemQuantity(skuId: string, quantity: number): GuestCartItem[] {
  const items = readGuestCart()
  const item = items.find(entry => entry.productSkuId === skuId)
  if (item) item.quantity = Math.min(99, Math.max(1, quantity))
  write(items)
  return items
}

export function removeGuestItem(skuId: string): GuestCartItem[] {
  const items = readGuestCart().filter(item => item.productSkuId !== skuId)
  write(items)
  return items
}

export function readPendingMergeSnapshot(): PendingMergeSnapshot | null {
  try {
    const raw = localStorage.getItem(PENDING_MERGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== 'object') return null
    const s = parsed as Record<string, unknown>
    if (typeof s.batchId !== 'string' || !Array.isArray(s.items)) return null
    const items = s.items.filter(validItem)
    if (items.length === 0) return null
    return {
      batchId: s.batchId,
      items,
      createdAt: typeof s.createdAt === 'number' ? s.createdAt : Date.now(),
    }
  } catch {
    return null
  }
}

export function createPendingMergeSnapshot(items: GuestCartItem[]): PendingMergeSnapshot {
  const batchId = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `batch_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`
  const snapshot: PendingMergeSnapshot = {
    batchId,
    items: items.map(item => ({ ...item })),
    createdAt: Date.now(),
  }
  localStorage.setItem(PENDING_MERGE_KEY, JSON.stringify(snapshot))
  return snapshot
}

export function clearPendingMergeSnapshot(): void {
  localStorage.removeItem(PENDING_MERGE_KEY)
}

function subtractMergedGuestItems(items: GuestCartItem[]): void {
  const current = readGuestCart()
  const mergedMap = new Map(items.map(m => [m.productSkuId, m.quantity]))
  const remaining: GuestCartItem[] = []

  for (const item of current) {
    const mergedQty = mergedMap.get(item.productSkuId)
    if (mergedQty === undefined) {
      remaining.push(item)
    } else if (item.quantity > mergedQty) {
      remaining.push({ ...item, quantity: item.quantity - mergedQty })
    }
  }

  localStorage.setItem(STORAGE_KEY, JSON.stringify(remaining))
  window.dispatchEvent(new Event('hcc:cart-changed'))
}

export function commitPendingMerge(snapshot: PendingMergeSnapshot): boolean {
  const activeSnapshot = readPendingMergeSnapshot()
  if (!activeSnapshot || activeSnapshot.batchId !== snapshot.batchId) return false

  subtractMergedGuestItems(snapshot.items)
  clearPendingMergeSnapshot()
  return true
}

export function getOrCreateGuestCartBatchId(): string {
  const snapshot = readPendingMergeSnapshot()
  if (snapshot) return snapshot.batchId
  const items = readGuestCart()
  if (items.length > 0) return createPendingMergeSnapshot(items).batchId
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `batch_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`
}

export function clearGuestCartBatchId(): void {
  clearPendingMergeSnapshot()
}

export function clearMergedGuestItems(merged: GuestCartItem[]): void {
  subtractMergedGuestItems(merged)
}

export function clearGuestCart() {
  clearPendingMergeSnapshot()
  write([])
}

export function setCartMergeNotice(message: string) { localStorage.setItem(NOTICE_KEY, message) }
export function takeCartMergeNotice(): string | null {
  const message = localStorage.getItem(NOTICE_KEY)
  localStorage.removeItem(NOTICE_KEY)
  return message
}
