import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type SkuInventorySummary = {
  skuId: string
  skuCode: string
  optionLabel: string
  productId: string
  productTitle: string
  physicalQty: number
  availableQty: number
  reservedQty: number
  allocatedQty: number
  skuStatus: string
  publicationStatus: string
}

export type InventoryMovement = {
  id: string
  productSkuId: string
  movementType: string
  qtyDelta: number
  sourceType: string
  sourceId: string
  actorId: string | null
  actorName: string | null
  note: string | null
  createdAt: string
}

export type StockInResult = {
  balanceId: string
  productSkuId: string
  physicalQty: number
  availableQty: number
  reservedQty: number
  allocatedQty: number
  revision: number
  movementId: string
}

export type InventoryAdjustment = {
  id: string
  productSkuId: string
  skuCode: string
  productTitle: string
  requestedBy: string
  requestedByName: string
  reviewedBy: string | null
  reviewedByName: string | null
  deltaQty: number
  reason: string
  reviewStatus: string
  createdAt: string
  reviewedAt: string | null
}

export type InventoryBalanceSnapshot = {
  balanceId: string
  productSkuId: string
  physicalQty: number
  availableQty: number
  reservedQty: number
  allocatedQty: number
  revision: number
}

export type InventoryAdjustmentDecisionResult = {
  request: InventoryAdjustment
  balance: InventoryBalanceSnapshot | null
  movementId: string | null
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) ? value : invalid() }
function integer(value: unknown, min: number): number {
  const parsed = number(value)
  return Number.isSafeInteger(parsed) && parsed >= min ? parsed : invalid()
}
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }

export function parseSkuInventorySummary(value: unknown): SkuInventorySummary {
  if (!isRecord(value)) return invalid()
  return {
    skuId: string(value.skuId),
    skuCode: string(value.skuCode),
    optionLabel: string(value.optionLabel),
    productId: string(value.productId),
    productTitle: string(value.productTitle),
    physicalQty: integer(value.physicalQty, 0),
    availableQty: integer(value.availableQty, 0),
    reservedQty: integer(value.reservedQty, 0),
    allocatedQty: integer(value.allocatedQty, 0),
    skuStatus: string(value.skuStatus),
    publicationStatus: string(value.publicationStatus),
  }
}

export function parseInventoryMovement(value: unknown): InventoryMovement {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    productSkuId: string(value.productSkuId),
    movementType: string(value.movementType),
    qtyDelta: integer(value.qtyDelta, -10000000),
    sourceType: string(value.sourceType),
    sourceId: string(value.sourceId),
    actorId: value.actorId === null || value.actorId === undefined ? null : string(value.actorId),
    actorName: value.actorName === null || value.actorName === undefined ? null : string(value.actorName),
    note: value.note === null || value.note === undefined ? null : string(value.note),
    createdAt: string(value.createdAt),
  }
}

export function parseStockInResult(value: unknown): StockInResult {
  if (!isRecord(value)) return invalid()
  return {
    balanceId: string(value.balanceId),
    productSkuId: string(value.productSkuId),
    physicalQty: integer(value.physicalQty, 0),
    availableQty: integer(value.availableQty, 0),
    reservedQty: integer(value.reservedQty, 0),
    allocatedQty: integer(value.allocatedQty, 0),
    revision: integer(value.revision, 0),
    movementId: string(value.movementId),
  }
}

export function parseInventoryAdjustment(value: unknown): InventoryAdjustment {
  if (!isRecord(value)) return invalid()
  const deltaQty = integer(value.deltaQty, -1_000_000)
  if (deltaQty >= 0) return invalid()
  return {
    id: string(value.id),
    productSkuId: string(value.productSkuId),
    skuCode: string(value.skuCode),
    productTitle: string(value.productTitle),
    requestedBy: string(value.requestedBy),
    requestedByName: string(value.requestedByName),
    reviewedBy: value.reviewedBy === null || value.reviewedBy === undefined ? null : string(value.reviewedBy),
    reviewedByName: value.reviewedByName === null || value.reviewedByName === undefined ? null : string(value.reviewedByName),
    deltaQty,
    reason: string(value.reason),
    reviewStatus: string(value.reviewStatus),
    createdAt: string(value.createdAt),
    reviewedAt: value.reviewedAt === null || value.reviewedAt === undefined ? null : string(value.reviewedAt),
  }
}

function parseBalanceSnapshot(value: unknown): InventoryBalanceSnapshot {
  if (!isRecord(value)) return invalid()
  return {
    balanceId: string(value.balanceId),
    productSkuId: string(value.productSkuId),
    physicalQty: integer(value.physicalQty, 0),
    availableQty: integer(value.availableQty, 0),
    reservedQty: integer(value.reservedQty, 0),
    allocatedQty: integer(value.allocatedQty, 0),
    revision: integer(value.revision, 0),
  }
}

async function sendAuthorizedMutation(path: string, method: 'POST', body: unknown, signal?: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: JSON.stringify(body),
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  })
}

export async function getInventorySummaries(signal?: AbortSignal): Promise<SkuInventorySummary[]> {
  const data = await apiRequest('/staff/inventory', { signal, cache: 'no-store' })
  return list(data, parseSkuInventorySummary)
}

export async function getSkuMovements(skuId: string, signal?: AbortSignal): Promise<InventoryMovement[]> {
  const data = await apiRequest(`/staff/inventory/${encodeURIComponent(skuId)}/movements`, { signal, cache: 'no-store' })
  return list(data, parseInventoryMovement)
}

export async function stockIn(
  data: { productSkuId: string; quantity: number; sourceType?: string; sourceId?: string; note?: string },
  signal?: AbortSignal
): Promise<StockInResult> {
  const res = await sendAuthorizedMutation('/staff/inventory/stock-in', 'POST', data, signal)
  return parseStockInResult(res)
}

export async function createAdjustmentRequest(
  data: { productSkuId: string; quantity: number; reason: string },
  signal?: AbortSignal,
): Promise<InventoryAdjustment> {
  const res = await sendAuthorizedMutation('/staff/inventory/adjustment-requests', 'POST', data, signal)
  return parseInventoryAdjustment(res)
}

export async function getAdjustmentRequests(signal?: AbortSignal): Promise<InventoryAdjustment[]> {
  const data = await apiRequest('/admin/inventory/adjustment-requests', { signal, cache: 'no-store' })
  return list(data, parseInventoryAdjustment)
}

export async function decideAdjustmentRequest(
  id: string,
  decision: 'APPROVE' | 'REJECT',
  signal?: AbortSignal,
): Promise<InventoryAdjustmentDecisionResult> {
  const value = await sendAuthorizedMutation(
    `/admin/inventory/adjustment-requests/${encodeURIComponent(id)}/decision`,
    'POST',
    { decision },
    signal,
  )
  if (!isRecord(value)) return invalid()
  return {
    request: parseInventoryAdjustment(value.request),
    balance: value.balance === null || value.balance === undefined ? null : parseBalanceSnapshot(value.balance),
    movementId: value.movementId === null || value.movementId === undefined ? null : string(value.movementId),
  }
}

