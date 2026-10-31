import type { LoaderFunctionArgs } from 'react-router-dom'
import { sessionLoader } from '../auth/auth.api'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { readGuestCart } from './guest-cart'
import type { GuestCartItem } from './guest-cart'
import { parseAddress } from '../account/account.api'

export type CartItem = {
  productSkuId: string
  productId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  unitPrice: number
  quantity: number
  lineTotal: number
  imageUrl: string | null
  availabilityCode: 'AVAILABLE' | 'INSUFFICIENT_STOCK' | 'SKU_NOT_AVAILABLE' | 'FLASH_SALE_NOT_AVAILABLE' | 'LOCAL_ONLY'
  flashSaleOfferId: string | null
}
export type Cart = {
  id: string | null
  items: CartItem[]
  totalQuantity: number
  subtotal: number
  canCheckout: boolean
  updatedAt: string | null
}
export type CartIssue = { productSkuId: string | null; code: string; message: string }
export type CartValidation = { isValid: boolean; cart: Cart; issues: CartIssue[] }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function amount(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown): number { const number = amount(value); return Number.isSafeInteger(number) ? number : invalid() }
function item(value: unknown): CartItem {
  if (!isRecord(value)) return invalid()
  const availability = text(value.availabilityCode)
  if (!['AVAILABLE', 'INSUFFICIENT_STOCK', 'SKU_NOT_AVAILABLE', 'FLASH_SALE_NOT_AVAILABLE'].includes(availability)) return invalid()
  return {
    productSkuId: text(value.productSkuId), productId: text(value.productId), productTitle: text(value.productTitle),
    skuCode: text(value.skuCode), optionLabel: text(value.optionLabel), unitPrice: amount(value.unitPrice),
    quantity: integer(value.quantity), lineTotal: amount(value.lineTotal),
    imageUrl: value.imageUrl === null ? null : text(value.imageUrl), availabilityCode: availability as CartItem['availabilityCode'],
    flashSaleOfferId: value.flashSaleOfferId === null || value.flashSaleOfferId === undefined ? null : text(value.flashSaleOfferId),
  }
}
function cart(value: unknown): Cart {
  if (!isRecord(value) || !Array.isArray(value.items) || typeof value.canCheckout !== 'boolean') return invalid()
  return {
    id: value.id === null ? null : text(value.id), items: value.items.map(item), totalQuantity: integer(value.totalQuantity),
    subtotal: amount(value.subtotal), canCheckout: value.canCheckout,
    updatedAt: value.updatedAt === null ? null : text(value.updatedAt),
  }
}
function issue(value: unknown): CartIssue {
  if (!isRecord(value)) return invalid()
  return { productSkuId: value.productSkuId === null ? null : text(value.productSkuId), code: text(value.code), message: text(value.message) }
}

export function guestCart(items: GuestCartItem[] = readGuestCart()): Cart {
  const mapped: CartItem[] = items.map(entry => ({ ...entry, flashSaleOfferId: entry.flashSaleOfferId ?? null, lineTotal: entry.unitPrice * entry.quantity, availabilityCode: 'LOCAL_ONLY' }))
  return { id: null, items: mapped, totalQuantity: mapped.reduce((sum, entry) => sum + entry.quantity, 0), subtotal: mapped.reduce((sum, entry) => sum + entry.lineTotal, 0), canCheckout: false, updatedAt: null }
}

async function csrfMutation(path: string, method: 'POST' | 'PUT' | 'DELETE', body?: unknown): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return apiRequest(path, { method, body: body === undefined ? undefined : JSON.stringify(body), headers: { 'X-CSRF-TOKEN': csrf.requestToken } })
}

export const getCustomerCart = () => apiRequest('/cart', { cache: 'no-store' }).then(cart)
export const setCustomerCartItem = (skuId: string, quantity: number, flashSaleOfferId?: string | null) =>
  csrfMutation(`/cart/items/${encodeURIComponent(skuId)}`, 'PUT', { quantity, flashSaleOfferId: flashSaleOfferId ?? null }).then(cart)
export const removeCustomerCartItem = (skuId: string) => csrfMutation(`/cart/items/${encodeURIComponent(skuId)}`, 'DELETE').then(cart)
export async function addCustomerCartItem(skuId: string, quantity = 1, flashSaleOfferId?: string | null): Promise<Cart> {
  const value = await csrfMutation('/cart/merge', 'POST', { items: [{ productSkuId: skuId, quantity, flashSaleOfferId: flashSaleOfferId ?? null }] })
  if (!isRecord(value)) return invalid()
  return cart(value.cart)
}
export async function validateCustomerCart(): Promise<CartValidation> {
  const value = await csrfMutation('/cart/validate', 'POST')
  if (!isRecord(value) || typeof value.isValid !== 'boolean' || !Array.isArray(value.issues)) return invalid()
  return { isValid: value.isValid, cart: cart(value.cart), issues: value.issues.map(issue) }
}

export async function cartLoader(args: LoaderFunctionArgs) {
  const user = await sessionLoader(args)
  if (user?.role === 'CUSTOMER') {
    const [customerCart, rawAddresses] = await Promise.all([
      getCustomerCart(),
      apiRequest('/account/addresses', { signal: args.request.signal, cache: 'no-store' }),
    ])
    if (!Array.isArray(rawAddresses)) return invalid()
    return { mode: 'customer' as const, cart: customerCart, addresses: rawAddresses.map(parseAddress) }
  }
  return { mode: 'guest' as const, cart: guestCart(), addresses: [] }
}
