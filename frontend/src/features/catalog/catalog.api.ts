import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type Category = { id: string; name: string; slug: string }
export type Brand = { id: string; name: string; slug: string; country: string | null; websiteUrl: string | null }
export type ProductSpecification = { key: string; displayName: string; value: string; displayOrder: number }
export type ProductSummary = {
  id: string
  title: string
  category: Category | null
  brand: Brand | null
  minPrice: number
  inStock: boolean
  imageUrl: string | null
  averageRating: number
  reviewCount: number
}
export type ProductSku = { id: string; skuCode: string; optionLabel: string; price: number; inStock: boolean }
export type ProductImage = { id: string; fileKey: string; altText: string | null; displayOrder: number }
export type ProductDetail = {
  id: string
  title: string
  description: string | null
  category: Category | null
  brand: Brand | null
  skus: ProductSku[]
  images: ProductImage[]
  specifications: ProductSpecification[]
  averageRating: number
  reviewCount: number
}
export type CatalogQueryMetadata = { normalizedText: string | null; effectiveSort: string }
export type CatalogFacet = { id: string; name: string; count: number }
export type CatalogFacets = {
  categories: CatalogFacet[]
  brands: CatalogFacet[]
  inStockCount: number
  priceMin: number | null
  priceMax: number | null
}
export type ProductPage = {
  items: ProductSummary[]
  totalCount: number
  page: number
  pageSize: number
  query: CatalogQueryMetadata | null
  facets: CatalogFacets | null
}
export type SearchSuggestionKind = 'PRODUCT' | 'SKU' | 'BRAND' | 'CATEGORY'
export type SearchSuggestion = {
  kind: SearchSuggestionKind
  label: string
  secondaryText: string | null
  productId: string | null
  queryValue: string
}

export type RelatedProductsResponse = {
  sourceProductId: string
  items: ProductSummary[]
}
export type ProductSummaryBatchResponse = {
  items: ProductSummary[]
  requestedCount: number
  omittedCount: number
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
const PRODUCT_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const EMPTY_PRODUCT_ID = '00000000-0000-0000-0000-000000000000'
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown, minimum: number): number {
  const parsed = number(value)
  return Number.isSafeInteger(parsed) && parsed >= minimum ? parsed : invalid()
}
function boolean(value: unknown): boolean { return typeof value === 'boolean' ? value : invalid() }
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }
function productId(value: unknown): string {
  const parsed = string(value).toLowerCase()
  return PRODUCT_ID_PATTERN.test(parsed) && parsed !== EMPTY_PRODUCT_ID ? parsed : invalid()
}
function discoveryImageUrl(value: unknown): string | null {
  if (value == null) return null
  const url = string(value).trim()
  if (!url || url.length > 2048 || url.startsWith('//') || url.includes('\\')) return null
  if (url.startsWith('/') || /^https?:\/\//i.test(url)) return url
  if (/^[a-z][a-z0-9+.-]*:/i.test(url)) return null
  return url
}
function category(value: unknown): Category {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), name: string(value.name), slug: string(value.slug) }
}
function brand(value: unknown): Brand {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), name: string(value.name), slug: string(value.slug), country: value.country == null ? null : string(value.country), websiteUrl: value.websiteUrl == null ? null : string(value.websiteUrl) }
}
function specification(value: unknown): ProductSpecification {
  if (!isRecord(value)) return invalid()
  return { key: string(value.key), displayName: string(value.displayName), value: string(value.value), displayOrder: integer(value.displayOrder, 0) }
}
function summary(value: unknown): ProductSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    category: value.category === null ? null : category(value.category),
    brand: value.brand === null || value.brand === undefined ? null : brand(value.brand),
    minPrice: number(value.minPrice),
    inStock: boolean(value.inStock),
    imageUrl: value.imageUrl === null || value.imageUrl === undefined ? null : string(value.imageUrl),
    averageRating: typeof value.averageRating === 'number' ? value.averageRating : 0,
    reviewCount: typeof value.reviewCount === 'number' ? value.reviewCount : 0,
  }
}

function discoverySummary(value: unknown): ProductSummary {
  if (!isRecord(value)) return invalid()
  const parsed = summary(value)
  const title = parsed.title.trim()
  const averageRating = number(value.averageRating)
  const reviewCount = integer(value.reviewCount, 0)
  if (!title || title.length > 300 || averageRating > 5) return invalid()
  return {
    ...parsed,
    id: productId(value.id),
    title,
    category: value.category === null ? null : {
      ...category(value.category),
      id: productId((value.category as Record<string, unknown>).id),
    },
    brand: value.brand === null ? null : {
      ...brand(value.brand),
      id: productId((value.brand as Record<string, unknown>).id),
    },
    imageUrl: discoveryImageUrl(value.imageUrl),
    averageRating,
    reviewCount,
  }
}
function sku(value: unknown): ProductSku {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), skuCode: string(value.skuCode), optionLabel: string(value.optionLabel), price: number(value.price), inStock: boolean(value.inStock) }
}
function productImage(value: unknown): ProductImage {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    fileKey: string(value.fileKey),
    altText: value.altText === null || value.altText === undefined ? null : string(value.altText),
    displayOrder: integer(value.displayOrder, 0),
  }
}
function nullableNumber(value: unknown): number | null {
  return value == null ? null : number(value)
}
function facet(value: unknown): CatalogFacet {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), name: string(value.name), count: integer(value.count, 0) }
}
function queryMetadata(value: unknown): CatalogQueryMetadata {
  if (!isRecord(value)) return invalid()
  return {
    normalizedText: value.normalizedText == null ? null : string(value.normalizedText),
    effectiveSort: string(value.effectiveSort),
  }
}
function facets(value: unknown): CatalogFacets {
  if (!isRecord(value)) return invalid()
  return {
    categories: list(value.categories, facet),
    brands: list(value.brands, facet),
    inStockCount: integer(value.inStockCount, 0),
    priceMin: nullableNumber(value.priceMin),
    priceMax: nullableNumber(value.priceMax),
  }
}
function page(value: unknown): ProductPage {
  if (!isRecord(value)) return invalid()
  return {
    items: list(value.items, summary),
    totalCount: integer(value.totalCount, 0),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    query: value.query == null ? null : queryMetadata(value.query),
    facets: value.facets == null ? null : facets(value.facets),
  }
}
export function parseProductDetail(value: unknown): ProductDetail {
  if (!isRecord(value)) return invalid()
  const skus = list(value.skus, sku)
  if (skus.length === 0) return invalid()
  const images = Array.isArray(value.images) ? list(value.images, productImage) : []
  const averageRating = value.averageRating === undefined ? 0 : number(value.averageRating)
  const reviewCount = value.reviewCount === undefined ? 0 : integer(value.reviewCount, 0)
  if (averageRating > 5) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    description: value.description === null ? null : string(value.description),
    category: value.category === null ? null : category(value.category),
    brand: value.brand === null || value.brand === undefined ? null : brand(value.brand),
    skus,
    images,
    specifications: Array.isArray(value.specifications) ? list(value.specifications, specification) : [],
    averageRating,
    reviewCount,
  }
}
function suggestion(value: unknown): SearchSuggestion {
  if (!isRecord(value)) return invalid()
  const kind = string(value.kind)
  if (kind !== 'PRODUCT' && kind !== 'SKU' && kind !== 'BRAND' && kind !== 'CATEGORY') return invalid()
  return {
    kind,
    label: string(value.label),
    secondaryText: value.secondaryText == null ? null : string(value.secondaryText),
    productId: value.productId == null ? null : string(value.productId),
    queryValue: string(value.queryValue),
  }
}

export function parseRelatedProducts(value: unknown): RelatedProductsResponse {
  if (!isRecord(value)) return invalid()
  const sourceProductId = productId(value.sourceProductId)
  const items = list(value.items, discoverySummary)
  const uniqueIds = new Set(items.map(item => item.id))
  if (items.length > 12 || uniqueIds.size !== items.length || uniqueIds.has(sourceProductId)) return invalid()
  return { sourceProductId, items }
}

export function parseProductSummaryBatch(value: unknown): ProductSummaryBatchResponse {
  if (!isRecord(value)) return invalid()
  const items = list(value.items, discoverySummary)
  const requestedCount = integer(value.requestedCount, 1)
  const omittedCount = integer(value.omittedCount, 0)
  if (requestedCount > 12 || items.length > requestedCount || items.length + omittedCount !== requestedCount) return invalid()
  if (new Set(items.map(item => item.id)).size !== items.length) return invalid()
  return { items, requestedCount, omittedCount }
}

export async function getRelatedProducts(productId: string, signal?: AbortSignal, limit = 6): Promise<RelatedProductsResponse> {
  const normalizedId = productId.toLowerCase()
  if (!PRODUCT_ID_PATTERN.test(normalizedId) || normalizedId === EMPTY_PRODUCT_ID || !Number.isInteger(limit) || limit < 1 || limit > 12) return invalid()
  const params = new URLSearchParams({ limit: limit.toString() })
  const result = parseRelatedProducts(await apiRequest(`/catalog/products/${encodeURIComponent(normalizedId)}/related?${params}`, { signal, cache: 'no-store' }))
  if (result.sourceProductId !== normalizedId || result.items.length > limit) return invalid()
  return result
}

export async function getProductSummaries(ids: readonly string[], signal?: AbortSignal): Promise<ProductSummaryBatchResponse> {
  if (ids.length === 0) {
    return { items: [], requestedCount: 0, omittedCount: 0 }
  }
  if (ids.length > 12) return invalid()
  const normalizedIds = ids.map(id => productId(id))
  if (new Set(normalizedIds).size !== normalizedIds.length) return invalid()
  const params = new URLSearchParams()
  for (const id of normalizedIds) {
    params.append('id', id)
  }
  const result = parseProductSummaryBatch(await apiRequest(`/catalog/products/batch?${params}`, { signal, cache: 'no-store' }))
  if (result.requestedCount !== normalizedIds.length) return invalid()
  let previousIndex = -1
  for (const item of result.items) {
    const currentIndex = normalizedIds.indexOf(item.id)
    if (currentIndex <= previousIndex) return invalid()
    previousIndex = currentIndex
  }
  return result
}

export async function catalogLoader({ request }: LoaderFunctionArgs) {
  const incoming = new URL(request.url).searchParams
  const query = new URLSearchParams()
  for (const key of ['q', 'categoryId', 'brandId', 'minPrice', 'maxPrice', 'inStock', 'minRating', 'sort', 'page', 'pageSize']) {
    const value = incoming.get(key)
    if (value?.trim()) query.set(key, value.trim())
  }
  const [categories, brands, products] = await Promise.all([
    apiRequest('/catalog/categories', { signal: request.signal }).then(value => list(value, category)),
    apiRequest('/catalog/brands', { signal: request.signal }).then(value => list(value, brand)),
    apiRequest(`/catalog/products?${query}`, { signal: request.signal }).then(page),
  ])
  return { categories, brands, products }
}

export async function getProductDetail(id: string, signal?: AbortSignal): Promise<ProductDetail> {
  return parseProductDetail(await apiRequest(`/catalog/products/${encodeURIComponent(id)}`, { signal, cache: 'no-store' }))
}

export async function getSearchSuggestions(query: string, signal?: AbortSignal): Promise<SearchSuggestion[]> {
  const params = new URLSearchParams({ q: query.trim(), limit: '8' })
  return list(await apiRequest(`/catalog/search/suggestions?${params}`, { signal }), suggestion)
}

export async function productLoader({ params, request }: LoaderFunctionArgs) {
  if (!params.id) throw new ApiError(404)
  return getProductDetail(params.id, request.signal)
}

export const formatPrice = (value: number) => new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' }).format(value)
