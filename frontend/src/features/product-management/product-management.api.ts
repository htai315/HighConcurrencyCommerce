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

export type ManagedProduct = {
  id: string
  title: string
  description: string | null
  categoryId: string | null
  publicationStatus: 'DRAFT' | 'PUBLISHED' | 'ARCHIVED' | string
  createdAt: string
  publishedAt: string | null
  skus: ManagedSku[]
  images: ManagedImage[]
}

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

export function parseManagedProduct(value: unknown): ManagedProduct {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    title: string(value.title),
    description: value.description === null || value.description === undefined ? null : string(value.description),
    categoryId: value.categoryId === null || value.categoryId === undefined ? null : string(value.categoryId),
    publicationStatus: string(value.publicationStatus),
    createdAt: string(value.createdAt),
    publishedAt: value.publishedAt === null || value.publishedAt === undefined ? null : string(value.publishedAt),
    skus: Array.isArray(value.skus) ? list(value.skus, parseSku) : [],
    images: Array.isArray(value.images) ? list(value.images, parseImage) : [],
  }
}

async function sendAuthorizedMutation(path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: unknown, signal?: AbortSignal): Promise<unknown> {
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

export async function createStaffDraft(data: { title: string; description?: string; categoryId?: string }, signal?: AbortSignal): Promise<ManagedProduct> {
  const res = await sendAuthorizedMutation('/staff/products', 'POST', data, signal)
  return parseManagedProduct(res)
}

export async function updateStaffDraft(id: string, data: { title: string; description?: string; categoryId?: string }, signal?: AbortSignal): Promise<ManagedProduct> {
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

export async function deleteStaffImage(productId: string, imageId: string, signal?: AbortSignal): Promise<void> {
  await sendAuthorizedMutation(`/staff/products/${productId}/images/${imageId}`, 'DELETE', undefined, signal)
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

export async function staffLoader(args: LoaderFunctionArgs) {
  await requireRole(['STAFF'])(args)
  return { products: await getStaffProducts(args.request.signal) }
}

export async function adminLoader(args: LoaderFunctionArgs) {
  await requireRole(['ADMIN'])(args)
  return { products: await getAdminProducts(args.request.signal) }
}

