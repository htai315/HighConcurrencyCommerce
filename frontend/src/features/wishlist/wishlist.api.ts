import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import type { Category } from '../catalog/catalog.api'
import { requireRole } from '../auth/auth.api'

export type WishlistAvailability = 'AVAILABLE' | 'OUT_OF_STOCK' | 'PRODUCT_UNAVAILABLE'

export interface WishlistAlertSummary {
  productId: string
  backInStockEnabled: boolean
  priceDropEnabled: boolean
  emailEnabled: boolean
  revision: number
}

export interface WishlistAlertSettings extends WishlistAlertSummary {
  currentAvailability: WishlistAvailability
  currentMinPrice: number | null
  updatedAt: string
}

export interface UpdateWishlistAlertSettings {
  backInStockEnabled: boolean
  priceDropEnabled: boolean
  emailEnabled: boolean
  expectedRevision: number
}

export interface WishlistProductSummary {
  id: string
  title: string
  category: Category | null
  minPrice: number
  inStock: boolean
  imageUrl: string | null
  averageRating: number
  reviewCount: number
}

export interface WishlistItem {
  productId: string
  addedAt: string
  availability: WishlistAvailability
  product: WishlistProductSummary | null
  alerts: WishlistAlertSummary
}

export interface WishlistPage {
  items: WishlistItem[]
  page: number
  pageSize: number
  totalCount: number
}

export interface WishlistSummary {
  productIds: string[]
  totalCount: number
  alerts: WishlistAlertSummary[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown, minimum: number): number {
  const parsed = number(value)
  return Number.isSafeInteger(parsed) && parsed >= minimum ? parsed : invalid()
}
function boolean(value: unknown): boolean { return typeof value === 'boolean' ? value : invalid() }
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }

function category(value: unknown): Category {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), name: string(value.name), slug: string(value.slug) }
}

function productSummary(value: unknown): WishlistProductSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    category: value.category === null || value.category === undefined ? null : category(value.category),
    minPrice: number(value.minPrice),
    inStock: boolean(value.inStock),
    imageUrl: value.imageUrl === null || value.imageUrl === undefined ? null : string(value.imageUrl),
    averageRating: typeof value.averageRating === 'number' ? value.averageRating : 0,
    reviewCount: typeof value.reviewCount === 'number' ? integer(value.reviewCount, 0) : 0,
  }
}

function alertSummary(value: unknown, fallbackProductId?: string): WishlistAlertSummary {
  if (value === null || value === undefined) {
    if (!fallbackProductId) return invalid()
    return { productId: fallbackProductId, backInStockEnabled: false, priceDropEnabled: false, emailEnabled: false, revision: 0 }
  }
  if (!isRecord(value)) return invalid()
  return {
    productId: string(value.productId),
    backInStockEnabled: boolean(value.backInStockEnabled),
    priceDropEnabled: boolean(value.priceDropEnabled),
    emailEnabled: boolean(value.emailEnabled),
    revision: integer(value.revision, 0),
  }
}

function alertSettings(value: unknown): WishlistAlertSettings {
  if (!isRecord(value)) return invalid()
  const summary = alertSummary(value)
  const availability = string(value.currentAvailability)
  if (availability !== 'AVAILABLE' && availability !== 'OUT_OF_STOCK' && availability !== 'PRODUCT_UNAVAILABLE') return invalid()
  return {
    ...summary,
    currentAvailability: availability,
    currentMinPrice: value.currentMinPrice === null || value.currentMinPrice === undefined ? null : number(value.currentMinPrice),
    updatedAt: string(value.updatedAt),
  }
}

function wishlistItem(value: unknown): WishlistItem {
  if (!isRecord(value)) return invalid()
  const availability = string(value.availability)
  if (availability !== 'AVAILABLE' && availability !== 'OUT_OF_STOCK' && availability !== 'PRODUCT_UNAVAILABLE') {
    return invalid()
  }
  return {
    productId: string(value.productId),
    addedAt: string(value.addedAt),
    availability: availability as WishlistAvailability,
    product: value.product === null || value.product === undefined ? null : productSummary(value.product),
    alerts: alertSummary(value.alerts, string(value.productId)),
  }
}

function wishlistPage(value: unknown): WishlistPage {
  if (!isRecord(value)) return invalid()
  return {
    items: list(value.items, wishlistItem),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount, 0),
  }
}

function wishlistSummary(value: unknown): WishlistSummary {
  if (!isRecord(value)) return invalid()
  return {
    productIds: list(value.productIds, string),
    totalCount: integer(value.totalCount, 0),
    alerts: value.alerts === undefined ? [] : list(value.alerts, item => alertSummary(item)),
  }
}

async function csrfMutation(path: string, method: 'PUT' | 'DELETE', signal?: AbortSignal): Promise<void> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  await apiRequest(path, {
    method,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    signal,
  })
  window.dispatchEvent(new Event('hcc:wishlist-changed'))
}

export async function getWishlist(page = 1, pageSize = 12, signal?: AbortSignal): Promise<WishlistPage> {
  const data = await apiRequest(`/wishlist?page=${page}&pageSize=${pageSize}`, { signal, cache: 'no-store' })
  return wishlistPage(data)
}

export async function getWishlistSummary(signal?: AbortSignal): Promise<WishlistSummary> {
  const data = await apiRequest('/wishlist/summary', { signal, cache: 'no-store' })
  return wishlistSummary(data)
}

export async function addToWishlist(productId: string, signal?: AbortSignal): Promise<void> {
  await csrfMutation(`/wishlist/items/${encodeURIComponent(productId)}`, 'PUT', signal)
}

export async function removeFromWishlist(productId: string, signal?: AbortSignal): Promise<void> {
  await csrfMutation(`/wishlist/items/${encodeURIComponent(productId)}`, 'DELETE', signal)
}

export async function getWishlistAlertSettings(productId: string, signal?: AbortSignal): Promise<WishlistAlertSettings> {
  const data = await apiRequest(`/wishlist/items/${encodeURIComponent(productId)}/alerts`, { signal, cache: 'no-store' })
  return alertSettings(data)
}

export async function updateWishlistAlertSettings(
  productId: string,
  request: UpdateWishlistAlertSettings,
  signal?: AbortSignal,
): Promise<WishlistAlertSettings> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  const data = await apiRequest(`/wishlist/items/${encodeURIComponent(productId)}/alerts`, {
    method: 'PUT',
    body: JSON.stringify(request),
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    signal,
    cache: 'no-store',
  })
  return alertSettings(data)
}

export async function wishlistLoader(args: LoaderFunctionArgs) {
  await requireRole(['CUSTOMER'])(args)
  const url = new URL(args.request.url)
  const page = parseInt(url.searchParams.get('page') ?? '1', 10) || 1
  return getWishlist(page, 12, args.request.signal)
}
