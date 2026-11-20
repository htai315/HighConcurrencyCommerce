import type { LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { requireRole } from '../auth/auth.api'

export type ManagedSku = {
  id: string
  productId: string
  skuCode: string
  optionLabel: string
  regularPrice: number
  skuStatus: string
}

export type ManagedImage = {
  id: string
  productId: string
  fileKey: string
  altText: string | null
  displayOrder: number
  imageStatus: string
}

export type ManagedSpecification = { id: string; key: string; displayName: string; value: string; displayOrder: number }
export type ManagedBrand = { id: string; name: string; slug: string; country: string | null; websiteUrl: string | null; isActive: boolean; productCount: number }

export type ManagedProduct = {
  id: string
  title: string
  description: string | null
  categoryId: string | null
  brandId: string | null
  publicationStatus: 'DRAFT' | 'PUBLISHED' | 'SUSPENDED' | 'ARCHIVED' | string
  createdAt: string
  publishedAt: string | null
  statusChangedAt?: string | null
  statusChangedBy?: string | null
  statusReason?: string | null
  skus: ManagedSku[]
  images: ManagedImage[]
  specifications: ManagedSpecification[]
}

export type ManagedCategory = { id: string; name: string; slug: string; isVisible: boolean; productCount: number }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown, min: number): number {
  const parsed = number(value)
  return Number.isSafeInteger(parsed) && parsed >= min ? parsed : invalid()
}
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }

function parseSku(value: unknown): ManagedSku {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    productId: string(value.productId),
    skuCode: string(value.skuCode),
    optionLabel: string(value.optionLabel),
    regularPrice: number(value.regularPrice),
    skuStatus: string(value.skuStatus),
  }
}

function parseImage(value: unknown): ManagedImage {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    productId: string(value.productId),
    fileKey: string(value.fileKey),
    altText: value.altText === null || value.altText === undefined ? null : string(value.altText),
    displayOrder: integer(value.displayOrder, 0),
    imageStatus: string(value.imageStatus),
  }
}

function parseSpecification(value: unknown): ManagedSpecification {
  if (!isRecord(value)) return invalid()
  return { id: string(value.id), key: string(value.key), displayName: string(value.displayName), value: string(value.value), displayOrder: integer(value.displayOrder, 0) }
}

export function parseManagedBrand(value: unknown): ManagedBrand {
  if (!isRecord(value) || typeof value.isActive !== 'boolean') return invalid()
  return { id: string(value.id), name: string(value.name), slug: string(value.slug), country: value.country == null ? null : string(value.country), websiteUrl: value.websiteUrl == null ? null : string(value.websiteUrl), isActive: value.isActive, productCount: integer(value.productCount, 0) }
}

export function parseManagedProduct(value: unknown): ManagedProduct {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    description: value.description === null || value.description === undefined ? null : string(value.description),
    categoryId: value.categoryId === null || value.categoryId === undefined ? null : string(value.categoryId),
    brandId: value.brandId === null || value.brandId === undefined ? null : string(value.brandId),
    publicationStatus: string(value.publicationStatus),
    createdAt: string(value.createdAt),
    publishedAt: value.publishedAt === null || value.publishedAt === undefined ? null : string(value.publishedAt),
    statusChangedAt: value.statusChangedAt === null || value.statusChangedAt === undefined ? null : string(value.statusChangedAt),
    statusChangedBy: value.statusChangedBy === null || value.statusChangedBy === undefined ? null : string(value.statusChangedBy),
    statusReason: value.statusReason === null || value.statusReason === undefined ? null : string(value.statusReason),
    skus: Array.isArray(value.skus) ? list(value.skus, parseSku) : [],
    images: Array.isArray(value.images) ? list(value.images, parseImage) : [],
    specifications: Array.isArray(value.specifications) ? list(value.specifications, parseSpecification) : [],
  }
}

function parseManagedCategory(value: unknown): ManagedCategory {
  if (!isRecord(value) || typeof value.isVisible !== 'boolean') return invalid()
  return { id: string(value.id), name: string(value.name), slug: string(value.slug), isVisible: value.isVisible, productCount: integer(value.productCount, 0) }
}

async function sendAuthorizedMutation(path: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', body?: unknown, signal?: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  })
}

// Staff APIs
export async function getStaffProducts(signal?: AbortSignal): Promise<ManagedProduct[]> {
  const data = await apiRequest('/staff/products', { signal, cache: 'no-store' })
  return list(data, parseManagedProduct)
}

export async function getStaffCategories(signal?: AbortSignal): Promise<ManagedCategory[]> {
  const data = await apiRequest('/staff/categories', { signal, cache: 'no-store' })
  return list(data, parseManagedCategory)
}

export async function getStaffBrands(signal?: AbortSignal): Promise<ManagedBrand[]> {
  const data = await apiRequest('/staff/brands', { signal, cache: 'no-store' })
  return list(data, parseManagedBrand)
}

export async function createStaffDraft(data: { title: string; description?: string; categoryId?: string; brandId?: string }, signal?: AbortSignal): Promise<ManagedProduct> {
  const res = await sendAuthorizedMutation('/staff/products', 'POST', data, signal)
  return parseManagedProduct(res)
}

export async function updateStaffDraft(id: string, data: { title: string; description?: string; categoryId?: string; brandId?: string }, signal?: AbortSignal): Promise<ManagedProduct> {
  const res = await sendAuthorizedMutation(`/staff/products/${id}/draft`, 'PATCH', data, signal)
  return parseManagedProduct(res)
}

export async function addStaffSku(productId: string, data: { skuCode: string; optionLabel: string }, signal?: AbortSignal): Promise<ManagedSku> {
  const res = await sendAuthorizedMutation(`/staff/products/${productId}/skus`, 'POST', data, signal)
  return parseSku(res)
}

export async function addStaffImage(productId: string, data: { fileKey: string; altText?: string; displayOrder: number }, signal?: AbortSignal): Promise<ManagedImage> {
  const res = await sendAuthorizedMutation(`/staff/products/${productId}/images`, 'POST', data, signal)
  return parseImage(res)
}

export async function uploadStaffImage(productId: string, file: File, data: { altText?: string; displayOrder: number }, signal?: AbortSignal): Promise<ManagedImage> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  const form = new FormData()
  form.append('file', file, file.name)
  if (data.altText) form.append('altText', data.altText)
  form.append('displayOrder', String(data.displayOrder))
  const value = await apiRequest(`/staff/products/${productId}/images/upload`, { method: 'POST', body: form, signal, headers: { 'X-CSRF-TOKEN': csrf.requestToken } })
  return parseImage(value)
}

export async function updateStaffImage(productId: string, imageId: string, data: { altText?: string; displayOrder: number }, signal?: AbortSignal): Promise<ManagedImage> {
  return parseImage(await sendAuthorizedMutation(`/staff/products/${productId}/images/${imageId}`, 'PATCH', data, signal))
}

export async function reorderStaffImages(productId: string, imageIds: string[], signal?: AbortSignal): Promise<ManagedImage[]> {
  return list(await sendAuthorizedMutation(`/staff/products/${productId}/images/order`, 'PUT', { imageIds }, signal), parseImage)
}

export async function deleteStaffImage(productId: string, imageId: string, signal?: AbortSignal): Promise<void> {
  await sendAuthorizedMutation(`/staff/products/${productId}/images/${imageId}`, 'DELETE', undefined, signal)
}

export async function upsertStaffSpecification(productId: string, key: string, data: { displayName: string; value: string; displayOrder: number }, signal?: AbortSignal): Promise<ManagedSpecification> {
  const res = await sendAuthorizedMutation(`/staff/products/${productId}/specifications/${encodeURIComponent(key)}`, 'PUT', data, signal)
  return parseSpecification(res)
}

export async function deleteStaffSpecification(productId: string, key: string, signal?: AbortSignal): Promise<void> {
  await sendAuthorizedMutation(`/staff/products/${productId}/specifications/${encodeURIComponent(key)}`, 'DELETE', undefined, signal)
}

// Admin APIs
export async function getAdminProducts(signal?: AbortSignal): Promise<ManagedProduct[]> {
  const data = await apiRequest('/admin/products', { signal, cache: 'no-store' })
  return list(data, parseManagedProduct)
}

export async function updateSkuPrice(skuId: string, price: number, signal?: AbortSignal): Promise<ManagedSku> {
  const res = await sendAuthorizedMutation(`/admin/skus/${skuId}/price`, 'PATCH', { price }, signal)
  return parseSku(res)
}

export async function publishProduct(productId: string, signal?: AbortSignal): Promise<{ id: string; publicationStatus: string; publishedAt: string }> {
  const res = await sendAuthorizedMutation(`/admin/products/${productId}/publish`, 'POST', {}, signal)
  if (!isRecord(res)) return invalid()
  return {
    id: string(res.id),
    publicationStatus: string(res.publicationStatus),
    publishedAt: string(res.publishedAt),
  }
}

export type ProductLifecycleResult = {
  productId: string
  publicationStatus: string
  statusChangedAt: string
  statusChangedBy: string | null
  statusReason: string | null
}

function parseLifecycle(value: unknown): ProductLifecycleResult {
  if (!isRecord(value)) return invalid()
  return {
    productId: string(value.productId),
    publicationStatus: string(value.publicationStatus),
    statusChangedAt: string(value.statusChangedAt),
    statusChangedBy: value.statusChangedBy == null ? null : string(value.statusChangedBy),
    statusReason: value.statusReason == null ? null : string(value.statusReason),
  }
}

export async function suspendProduct(productId: string, reason: string, signal?: AbortSignal): Promise<ProductLifecycleResult> {
  return parseLifecycle(await sendAuthorizedMutation(`/admin/products/${productId}/suspend`, 'POST', { reason }, signal))
}

export async function resumeProduct(productId: string, signal?: AbortSignal): Promise<ProductLifecycleResult> {
  return parseLifecycle(await sendAuthorizedMutation(`/admin/products/${productId}/resume`, 'POST', {}, signal))
}

export async function archiveProduct(productId: string, reason: string, signal?: AbortSignal): Promise<ProductLifecycleResult> {
  return parseLifecycle(await sendAuthorizedMutation(`/admin/products/${productId}/archive`, 'POST', { reason }, signal))
}

export async function staffLoader(args: LoaderFunctionArgs) {
  await requireRole(['STAFF'])(args)
  const [products, categories, brands] = await Promise.all([getStaffProducts(args.request.signal), getStaffCategories(args.request.signal), getStaffBrands(args.request.signal)])
  return { products, categories, brands }
}

export async function adminLoader(args: LoaderFunctionArgs) {
  await requireRole(['ADMIN'])(args)
  return { products: await getAdminProducts(args.request.signal) }
}

