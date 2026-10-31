const STORAGE_KEY = 'hcc.guest-cart.v1'
const NOTICE_KEY = 'hcc.cart-merge-notice.v1'

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
  if (existing) existing.quantity = Math.min(99, existing.quantity + quantity)
  else items.push({ ...item, quantity: Math.min(99, Math.max(1, quantity)) })
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

export function clearGuestCart() { write([]) }
export function setCartMergeNotice(message: string) { localStorage.setItem(NOTICE_KEY, message) }
export function takeCartMergeNotice(): string | null {
  const message = localStorage.getItem(NOTICE_KEY)
  localStorage.removeItem(NOTICE_KEY)
  return message
}
