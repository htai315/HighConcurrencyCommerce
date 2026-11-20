import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type ShippingMethodStatus = 'ACTIVE' | 'INACTIVE'
export type AdminShippingMethodSummary = {
  id: string; code: string; name: string; methodStatus: ShippingMethodStatus; baseFee: number; freeShippingThreshold: number | null
  estimatedMinDays: number; estimatedMaxDays: number; isDefault: boolean; displayOrder: number; revision: number; provinceRateCount: number; updatedAt: string
}
export type AdminShippingProvinceRate = { id: string; provinceKey: string; surchargeFee: number; isActive: boolean; updatedAt: string }
export type AdminShippingMethodDetail = AdminShippingMethodSummary & { description: string | null; createdAt: string; provinceRates: AdminShippingProvinceRate[] }
export type ShippingMethodPayload = {
  code?: string; name: string; description: string | null; baseFee: number; freeShippingThreshold: number | null
  estimatedMinDays: number; estimatedMaxDays: number; displayOrder: number; expectedRevision?: number
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' && value.trim().length > 0 ? value : invalid() }
function nullableText(value: unknown): string | null { return value == null ? null : text(value) }
function money(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid() }
function integer(value: unknown, min = 0): number { const result = money(value); return Number.isSafeInteger(result) && result >= min ? result : invalid() }
function status(value: unknown): ShippingMethodStatus { const result = text(value); return result === 'ACTIVE' || result === 'INACTIVE' ? result : invalid() }

export function parseAdminShippingMethodSummary(value: unknown): AdminShippingMethodSummary {
  if (!isRecord(value) || typeof value.isDefault !== 'boolean') return invalid()
  const min = integer(value.estimatedMinDays)
  const max = integer(value.estimatedMaxDays)
  if (max < min) return invalid()
  return {
    id: text(value.id), code: text(value.code), name: text(value.name), methodStatus: status(value.methodStatus),
    baseFee: money(value.baseFee), freeShippingThreshold: value.freeShippingThreshold == null ? null : money(value.freeShippingThreshold),
    estimatedMinDays: min, estimatedMaxDays: max, isDefault: value.isDefault, displayOrder: integer(value.displayOrder),
    revision: integer(value.revision, 1), provinceRateCount: integer(value.provinceRateCount), updatedAt: text(value.updatedAt),
  }
}

export function parseAdminShippingMethodDetail(value: unknown): AdminShippingMethodDetail {
  if (!isRecord(value) || !Array.isArray(value.provinceRates)) return invalid()
  const summary = parseAdminShippingMethodSummary({ ...value, provinceRateCount: value.provinceRates.length })
  return {
    ...summary, description: nullableText(value.description), createdAt: text(value.createdAt),
    provinceRates: value.provinceRates.map(rate => {
      if (!isRecord(rate) || typeof rate.isActive !== 'boolean') return invalid()
      return { id: text(rate.id), provinceKey: text(rate.provinceKey), surchargeFee: money(rate.surchargeFee), isActive: rate.isActive, updatedAt: text(rate.updatedAt) }
    }),
  }
}

async function csrf(): Promise<string> {
  const value = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(value) || typeof value.requestToken !== 'string') return invalid()
  return value.requestToken
}

async function mutate(path: string, method: 'POST' | 'PATCH' | 'PUT' | 'DELETE', body?: unknown): Promise<AdminShippingMethodDetail> {
  const token = await csrf()
  return parseAdminShippingMethodDetail(await apiRequest(path, {
    method, headers: { 'X-CSRF-TOKEN': token }, body: body === undefined ? undefined : JSON.stringify(body),
  }))
}

export async function listAdminShippingMethods(): Promise<AdminShippingMethodSummary[]> {
  const value = await apiRequest('/admin/shipping-methods', { cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseAdminShippingMethodSummary)
}

export async function getAdminShippingMethod(id: string): Promise<AdminShippingMethodDetail> {
  return parseAdminShippingMethodDetail(await apiRequest(`/admin/shipping-methods/${encodeURIComponent(id)}`, { cache: 'no-store' }))
}

export function createAdminShippingMethod(payload: ShippingMethodPayload): Promise<AdminShippingMethodDetail> {
  return mutate('/admin/shipping-methods', 'POST', payload)
}

export function updateAdminShippingMethod(id: string, payload: ShippingMethodPayload): Promise<AdminShippingMethodDetail> {
  return mutate(`/admin/shipping-methods/${encodeURIComponent(id)}`, 'PATCH', payload)
}

export function transitionAdminShippingMethod(id: string, action: 'activate' | 'deactivate' | 'make-default', expectedRevision: number): Promise<AdminShippingMethodDetail> {
  return mutate(`/admin/shipping-methods/${encodeURIComponent(id)}/${action}`, 'POST', { expectedRevision })
}

export function upsertAdminShippingProvinceRate(id: string, province: string, surchargeFee: number, isActive: boolean, expectedRevision: number): Promise<AdminShippingMethodDetail> {
  return mutate(`/admin/shipping-methods/${encodeURIComponent(id)}/province-rates/${encodeURIComponent(province)}`, 'PUT', { surchargeFee, isActive, expectedRevision })
}

export function removeAdminShippingProvinceRate(id: string, province: string, expectedRevision: number): Promise<AdminShippingMethodDetail> {
  return mutate(`/admin/shipping-methods/${encodeURIComponent(id)}/province-rates/${encodeURIComponent(province)}?expectedRevision=${expectedRevision}`, 'DELETE')
}
