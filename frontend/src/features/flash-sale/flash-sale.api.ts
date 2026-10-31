import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type FlashSaleCampaignStatus = 'DRAFT' | 'SCHEDULED' | 'ACTIVE' | 'ENDED' | 'CANCELLED'

export interface FlashSaleOffer {
  id: string
  campaignId: string
  productSkuId: string
  productId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  regularPrice: number
  salePrice: number
  quotaTotal: number
  quotaHeld: number
  quotaSold: number
  remainingQuota: number
  perCustomerLimit: number
}

export interface FlashSaleCampaignSummary {
  id: string
  name: string
  startsAt: string
  endsAt: string
  campaignStatus: FlashSaleCampaignStatus
  createdBy: string
  createdAt: string
  updatedAt: string
  offersCount: number
  totalQuota: number
  totalSold: number
}

export interface FlashSaleCampaignDetail {
  id: string
  name: string
  startsAt: string
  endsAt: string
  campaignStatus: FlashSaleCampaignStatus
  createdBy: string
  createdAt: string
  updatedAt: string
  offers: FlashSaleOffer[]
}

export interface FlashSalePerformance {
  campaignId: string
  name: string
  status: string
  startsAt: string
  endsAt: string
  totalQuota: number
  totalHeld: number
  totalSold: number
  remainingQuota: number
  estimatedRevenue: number
  offers: {
    offerId: string
    skuCode: string
    productTitle: string
    salePrice: number
    quotaTotal: number
    quotaHeld: number
    quotaSold: number
    remainingQuota: number
    soldRevenue: number
  }[]
}

export interface PublicFlashSaleOffer {
  offerId: string
  productSkuId: string
  productId: string
  productTitle: string
  skuCode: string
  optionLabel: string
  regularPrice: number
  salePrice: number
  discountPercentage: number
  quotaTotal: number
  remainingQuota: number
  perCustomerLimit: number
  imageUrl: string | null
}

export interface PublicFlashSaleCampaign {
  id: string
  name: string
  startsAt: string
  endsAt: string
  campaignStatus: string
  offers: PublicFlashSaleOffer[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) ? value : invalid() }
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }

function parseOffer(value: unknown): FlashSaleOffer {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    campaignId: string(value.campaignId),
    productSkuId: string(value.productSkuId),
    productId: string(value.productId),
    productTitle: string(value.productTitle),
    skuCode: string(value.skuCode),
    optionLabel: string(value.optionLabel),
    regularPrice: number(value.regularPrice),
    salePrice: number(value.salePrice),
    quotaTotal: number(value.quotaTotal),
    quotaHeld: number(value.quotaHeld),
    quotaSold: number(value.quotaSold),
    remainingQuota: number(value.remainingQuota),
    perCustomerLimit: number(value.perCustomerLimit),
  }
}

function parseSummary(value: unknown): FlashSaleCampaignSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    name: string(value.name),
    startsAt: string(value.startsAt),
    endsAt: string(value.endsAt),
    campaignStatus: string(value.campaignStatus) as FlashSaleCampaignStatus,
    createdBy: string(value.createdBy),
    createdAt: string(value.createdAt),
    updatedAt: string(value.updatedAt),
    offersCount: number(value.offersCount),
    totalQuota: number(value.totalQuota),
    totalSold: number(value.totalSold),
  }
}

function parseDetail(value: unknown): FlashSaleCampaignDetail {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    name: string(value.name),
    startsAt: string(value.startsAt),
    endsAt: string(value.endsAt),
    campaignStatus: string(value.campaignStatus) as FlashSaleCampaignStatus,
    createdBy: string(value.createdBy),
    createdAt: string(value.createdAt),
    updatedAt: string(value.updatedAt),
    offers: list(value.offers, parseOffer),
  }
}

async function sendMutation(path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: unknown, signal?: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken, 'Content-Type': 'application/json' },
  })
}

// Admin APIs
export async function getAdminFlashSales(signal?: AbortSignal): Promise<FlashSaleCampaignSummary[]> {
  const data = await apiRequest('/admin/flash-sales', { signal, cache: 'no-store' })
  return list(data, parseSummary)
}

export async function getAdminFlashSale(id: string, signal?: AbortSignal): Promise<FlashSaleCampaignDetail> {
  const data = await apiRequest(`/admin/flash-sales/${id}`, { signal, cache: 'no-store' })
  return parseDetail(data)
}

export async function createFlashSale(
  data: { name: string; startsAt: string; endsAt: string },
  signal?: AbortSignal
): Promise<FlashSaleCampaignDetail> {
  const res = await sendMutation('/admin/flash-sales', 'POST', data, signal)
  return parseDetail(res)
}

export async function updateFlashSale(
  id: string,
  data: { name: string; startsAt: string; endsAt: string },
  signal?: AbortSignal
): Promise<FlashSaleCampaignDetail> {
  const res = await sendMutation(`/admin/flash-sales/${id}`, 'PATCH', data, signal)
  return parseDetail(res)
}

export async function addFlashSaleOffer(
  campaignId: string,
  data: { productSkuId: string; salePrice: number; quotaTotal: number; perCustomerLimit: number },
  signal?: AbortSignal
): Promise<FlashSaleOffer> {
  const res = await sendMutation(`/admin/flash-sales/${campaignId}/offers`, 'POST', data, signal)
  return parseOffer(res)
}

export async function deleteFlashSaleOffer(
  campaignId: string,
  offerId: string,
  signal?: AbortSignal
): Promise<void> {
  await sendMutation(`/admin/flash-sales/${campaignId}/offers/${offerId}`, 'DELETE', undefined, signal)
}

export async function publishFlashSale(id: string, signal?: AbortSignal): Promise<FlashSaleCampaignDetail> {
  const res = await sendMutation(`/admin/flash-sales/${id}/publish`, 'POST', {}, signal)
  return parseDetail(res)
}

export async function cancelFlashSale(
  id: string,
  reason?: string,
  signal?: AbortSignal
): Promise<FlashSaleCampaignDetail> {
  const res = await sendMutation(`/admin/flash-sales/${id}/cancel`, 'POST', reason ? { reason } : {}, signal)
  return parseDetail(res)
}

export async function getFlashSalePerformance(id: string, signal?: AbortSignal): Promise<FlashSalePerformance> {
  const data = await apiRequest(`/admin/flash-sales/${id}/performance`, { signal, cache: 'no-store' })
  if (!isRecord(data)) return invalid()
  return {
    campaignId: string(data.campaignId),
    name: string(data.name),
    status: string(data.status),
    startsAt: string(data.startsAt),
    endsAt: string(data.endsAt),
    totalQuota: number(data.totalQuota),
    totalHeld: number(data.totalHeld),
    totalSold: number(data.totalSold),
    remainingQuota: number(data.remainingQuota),
    estimatedRevenue: number(data.estimatedRevenue),
    offers: Array.isArray(data.offers)
      ? data.offers.map(o => {
          if (!isRecord(o)) return invalid()
          return {
            offerId: string(o.offerId),
            skuCode: string(o.skuCode),
            productTitle: string(o.productTitle),
            salePrice: number(o.salePrice),
            quotaTotal: number(o.quotaTotal),
            quotaHeld: number(o.quotaHeld),
            quotaSold: number(o.quotaSold),
            remainingQuota: number(o.remainingQuota),
            soldRevenue: number(o.soldRevenue),
          }
        })
      : [],
  }
}

// Public APIs
export async function getPublicFlashSales(signal?: AbortSignal): Promise<PublicFlashSaleCampaign[]> {
  const data = await apiRequest('/catalog/flash-sales', { signal, cache: 'no-store' })
  if (!Array.isArray(data)) return []
  return data.map(c => {
    if (!isRecord(c)) return invalid()
    return {
      id: string(c.id),
      name: string(c.name),
      startsAt: string(c.startsAt),
      endsAt: string(c.endsAt),
      campaignStatus: string(c.campaignStatus),
      offers: Array.isArray(c.offers)
        ? c.offers.map(o => {
            if (!isRecord(o)) return invalid()
            return {
              offerId: string(o.offerId),
              productSkuId: string(o.productSkuId),
              productId: string(o.productId),
              productTitle: string(o.productTitle),
              skuCode: string(o.skuCode),
              optionLabel: string(o.optionLabel),
              regularPrice: number(o.regularPrice),
              salePrice: number(o.salePrice),
              discountPercentage: number(o.discountPercentage),
              quotaTotal: number(o.quotaTotal),
              remainingQuota: number(o.remainingQuota),
              perCustomerLimit: number(o.perCustomerLimit),
              imageUrl: typeof o.imageUrl === 'string' ? o.imageUrl : null,
            }
          })
        : [],
    }
  })
}
