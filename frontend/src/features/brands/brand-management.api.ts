import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export interface AdminBrand { id: string; name: string; slug: string; country: string | null; websiteUrl: string | null; isActive: boolean; productCount: number; createdAt: string; updatedAt: string }
export interface BrandMutation { name: string; slug: string; country?: string; websiteUrl?: string; isActive: boolean }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function parse(value: unknown): AdminBrand {
  if (!isRecord(value) || typeof value.id !== 'string' || typeof value.name !== 'string' || typeof value.slug !== 'string' || typeof value.isActive !== 'boolean' || typeof value.productCount !== 'number' || typeof value.createdAt !== 'string' || typeof value.updatedAt !== 'string') return invalid()
  return { id: value.id, name: value.name, slug: value.slug, country: value.country == null ? null : String(value.country), websiteUrl: value.websiteUrl == null ? null : String(value.websiteUrl), isActive: value.isActive, productCount: value.productCount, createdAt: value.createdAt, updatedAt: value.updatedAt }
}
async function csrf() { const value = await apiRequest('/auth/csrf', { cache: 'no-store' }); return isRecord(value) && typeof value.requestToken === 'string' ? value.requestToken : invalid() }
async function mutation(path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: BrandMutation) {
  const value = await apiRequest(path, { method, body: body ? JSON.stringify(body) : undefined, headers: { 'X-CSRF-TOKEN': await csrf() } })
  return method === 'DELETE' ? undefined : parse(value)
}
export async function listAdminBrands(): Promise<AdminBrand[]> { const value = await apiRequest('/admin/brands', { cache: 'no-store' }); return Array.isArray(value) ? value.map(parse) : invalid() }
export const createAdminBrand = (body: BrandMutation) => mutation('/admin/brands', 'POST', body)
export const updateAdminBrand = (id: string, body: BrandMutation) => mutation(`/admin/brands/${encodeURIComponent(id)}`, 'PATCH', body)
export const deleteAdminBrand = (id: string) => mutation(`/admin/brands/${encodeURIComponent(id)}`, 'DELETE')
export function slugifyBrand(value: string) { return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/gi, 'd').toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') }
export function brandError(error: unknown, fallback: string) {
  if (error instanceof ApiError) return ({ BRAND_SLUG_CONFLICT: 'Slug thương hiệu đã tồn tại.', BRAND_IN_USE: 'Thương hiệu đang được gắn với sản phẩm; hãy ngừng kích hoạt thay vì xóa.', BRAND_NOT_FOUND: 'Thương hiệu không còn tồn tại.' } as Record<string, string>)[error.code ?? ''] ?? fallback
  return error instanceof Error ? error.message : fallback
}
