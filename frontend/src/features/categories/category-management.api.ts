import { apiRequest, isRecord, ApiError } from '../../lib/api/client'

export interface AdminCategory {
  id: string
  name: string
  slug: string
  isVisible: boolean
  productCount: number
  createdAt: string
  updatedAt: string
}

export interface CategoryMutation { name: string; slug: string; isVisible: boolean }

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function parseCategory(value: unknown): AdminCategory {
  if (!isRecord(value) || typeof value.id !== 'string' || typeof value.name !== 'string' || typeof value.slug !== 'string'
    || typeof value.isVisible !== 'boolean' || typeof value.productCount !== 'number'
    || typeof value.createdAt !== 'string' || typeof value.updatedAt !== 'string') return invalid()
  if (!Number.isSafeInteger(value.productCount) || value.productCount < 0) return invalid()
  return { id: value.id, name: value.name, slug: value.slug, isVisible: value.isVisible, productCount: value.productCount, createdAt: value.createdAt, updatedAt: value.updatedAt }
}

async function csrf(): Promise<string> {
  const value = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(value) || typeof value.requestToken !== 'string') return invalid()
  return value.requestToken
}

async function mutate(path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: CategoryMutation): Promise<AdminCategory | undefined> {
  const token = await csrf()
  const value = await apiRequest(path, { method, body: body ? JSON.stringify(body) : undefined, headers: { 'X-CSRF-TOKEN': token } })
  return method === 'DELETE' ? undefined : parseCategory(value)
}

export async function listAdminCategories(): Promise<AdminCategory[]> {
  const value = await apiRequest('/admin/categories', { cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseCategory)
}
export const createAdminCategory = (body: CategoryMutation) => mutate('/admin/categories', 'POST', body) as Promise<AdminCategory>
export const updateAdminCategory = (id: string, body: CategoryMutation) => mutate(`/admin/categories/${encodeURIComponent(id)}`, 'PATCH', body) as Promise<AdminCategory>
export const deleteAdminCategory = (id: string) => mutate(`/admin/categories/${encodeURIComponent(id)}`, 'DELETE')

const errorMessages: Record<string, string> = {
  CATEGORY_SLUG_CONFLICT: 'Slug này đã được một danh mục khác sử dụng.',
  CATEGORY_IN_USE: 'Danh mục đang có sản phẩm; hãy ẩn danh mục thay vì xóa.',
  CATEGORY_NOT_FOUND: 'Danh mục không còn tồn tại.',
  VALIDATION_ERROR: 'Tên hoặc slug danh mục chưa đúng định dạng.',
}
export function categoryErrorText(error: unknown, fallback: string): string {
  return error instanceof ApiError && error.code ? errorMessages[error.code] ?? fallback : error instanceof Error ? error.message : fallback
}

export function slugifyCategory(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'd')
    .toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
}
