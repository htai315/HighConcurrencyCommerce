import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type Category = { id: string; name: string; slug: string }
export type ProductSummary = {
  id: string
  title: string
  category: Category | null
  minPrice: number
  inStock: boolean
  imageUrl: string | null
}
export type ProductSku = { id: string; skuCode: string; optionLabel: string; price: number; inStock: boolean }
export type ProductImage = { id: string; fileKey: string; altText: string | null; displayOrder: number }
export type ProductDetail = {
  id: string
  title: string
  description: string | null
  category: Category | null
  skus: ProductSku[]
  images: ProductImage[]
}
export type ProductPage = { items: ProductSummary[]; totalCount: number; page: number; pageSize: number }

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
function summary(value: unknown): ProductSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    category: value.category === null ? null : category(value.category),
    minPrice: number(value.minPrice),
    inStock: boolean(value.inStock),
    imageUrl: value.imageUrl === null || value.imageUrl === undefined ? null : string(value.imageUrl),
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
function page(value: unknown): ProductPage {
  if (!isRecord(value)) return invalid()
  return { items: list(value.items, summary), totalCount: integer(value.totalCount, 0), page: integer(value.page, 1), pageSize: integer(value.pageSize, 1) }
}
function detail(value: unknown): ProductDetail {
  if (!isRecord(value)) return invalid()
  const skus = list(value.skus, sku)
  if (skus.length === 0) return invalid()
  const images = Array.isArray(value.images) ? list(value.images, productImage) : []
  return {
    id: string(value.id),
    title: string(value.title),
    description: value.description === null ? null : string(value.description),
    category: value.category === null ? null : category(value.category),
    skus,
    images,
  }
}

export async function catalogLoader({ request }: LoaderFunctionArgs) {
  const incoming = new URL(request.url).searchParams
  const query = new URLSearchParams()
  for (const key of ['q', 'categoryId', 'minPrice', 'maxPrice', 'sort', 'page', 'pageSize']) {
    const value = incoming.get(key)
    if (value?.trim()) query.set(key, value.trim())
  }
  const [categories, products] = await Promise.all([
    apiRequest('/catalog/categories', { signal: request.signal }).then(value => list(value, category)),
    apiRequest(`/catalog/products?${query}`, { signal: request.signal }).then(page),
  ])
  return { categories, products }
}

export async function productLoader({ params, request }: LoaderFunctionArgs) {
  if (!params.id) throw new ApiError(404)
  return detail(await apiRequest(`/catalog/products/${encodeURIComponent(params.id)}`, { signal: request.signal }))
}

export const formatPrice = (value: number) => new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' }).format(value)
