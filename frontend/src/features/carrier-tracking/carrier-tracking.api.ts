import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type CarrierStatus = 'ACTIVE' | 'INACTIVE'
export type ConsignmentStatus = 'DRAFT' | 'LABEL_READY' | 'HANDED_OVER' | 'IN_TRANSIT' | 'OUT_FOR_DELIVERY' | 'DELIVERY_FAILED' | 'DELIVERED' | 'RETURNING' | 'RETURNED' | 'CANCELLED'

export type CarrierSummary = {
  id: string; code: string; name: string; hotline: string | null; trackingUrlTemplate: string | null
  carrierStatus: CarrierStatus; revision: number; consignmentCount: number; updatedAt: string
}
export type CarrierDetail = Omit<CarrierSummary, 'consignmentCount'> & { createdAt: string }
export type CarrierPayload = { code?: string; name: string; hotline: string | null; trackingUrlTemplate: string | null; expectedRevision?: number }
export type CarrierLabel = { contentType: string; sizeBytes: number; sha256: string }
export type CarrierTrackingEvent = {
  id: string; eventKey: string; eventType: string; eventStatus: string; location: string | null
  description: string | null; occurredAt: string; recordedAt: string; sourceType: string
}
export type CarrierConsignment = {
  id: string; targetType: 'SALES' | 'EXCHANGE'; targetShipmentId: string; carrierId: string; carrierCode: string
  carrierName: string; trackingNumber: string; trackingUrl: string | null; consignmentStatus: ConsignmentStatus
  serviceCode: string | null; recipientName: string; recipientPhone: string; destination: string
  shippingMethodCode: string | null; shippingMethodName: string | null; label: CarrierLabel | null; revision: number
  handedOverAt: string | null; deliveredAt: string | null; cancelledAt: string | null; createdAt: string
  updatedAt: string; events: CarrierTrackingEvent[]
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function text(value: unknown): string { return typeof value === 'string' && value.trim().length > 0 ? value : invalid() }
function nullableText(value: unknown): string | null { return value == null || value === '' ? null : text(value) }
function integer(value: unknown, min = 0): number { return typeof value === 'number' && Number.isSafeInteger(value) && value >= min ? value : invalid() }
function carrierStatus(value: unknown): CarrierStatus { return value === 'ACTIVE' || value === 'INACTIVE' ? value : invalid() }
function consignmentStatus(value: unknown): ConsignmentStatus {
  const allowed: ConsignmentStatus[] = ['DRAFT', 'LABEL_READY', 'HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY', 'DELIVERY_FAILED', 'DELIVERED', 'RETURNING', 'RETURNED', 'CANCELLED']
  return typeof value === 'string' && allowed.includes(value as ConsignmentStatus) ? value as ConsignmentStatus : invalid()
}

export function parseCarrierSummary(value: unknown): CarrierSummary {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id), code: text(value.code), name: text(value.name), hotline: nullableText(value.hotline),
    trackingUrlTemplate: nullableText(value.trackingUrlTemplate), carrierStatus: carrierStatus(value.carrierStatus),
    revision: integer(value.revision, 1), consignmentCount: integer(value.consignmentCount), updatedAt: text(value.updatedAt),
  }
}

export function parseCarrierDetail(value: unknown): CarrierDetail {
  if (!isRecord(value)) return invalid()
  const summary = parseCarrierSummary({ ...value, consignmentCount: 0 })
  const { consignmentCount: _ignored, ...detail } = summary
  return { ...detail, createdAt: text(value.createdAt) }
}

function parseTrackingEvent(value: unknown): CarrierTrackingEvent {
  if (!isRecord(value)) return invalid()
  return {
    id: text(value.id), eventKey: text(value.eventKey), eventType: text(value.eventType), eventStatus: text(value.eventStatus),
    location: nullableText(value.location), description: nullableText(value.description), occurredAt: text(value.occurredAt),
    recordedAt: text(value.recordedAt), sourceType: text(value.sourceType),
  }
}

export function parseCarrierConsignment(value: unknown): CarrierConsignment {
  if (!isRecord(value) || !Array.isArray(value.events)) return invalid()
  const targetType = value.targetType === 'SALES' || value.targetType === 'EXCHANGE' ? value.targetType : invalid()
  let label: CarrierLabel | null = null
  if (value.label != null) {
    if (!isRecord(value.label)) return invalid()
    label = { contentType: text(value.label.contentType), sizeBytes: integer(value.label.sizeBytes, 1), sha256: text(value.label.sha256) }
  }
  return {
    id: text(value.id), targetType, targetShipmentId: text(value.targetShipmentId), carrierId: text(value.carrierId),
    carrierCode: text(value.carrierCode), carrierName: text(value.carrierName), trackingNumber: text(value.trackingNumber),
    trackingUrl: nullableText(value.trackingUrl), consignmentStatus: consignmentStatus(value.consignmentStatus),
    serviceCode: nullableText(value.serviceCode), recipientName: text(value.recipientName), recipientPhone: text(value.recipientPhone),
    destination: text(value.destination), shippingMethodCode: nullableText(value.shippingMethodCode),
    shippingMethodName: nullableText(value.shippingMethodName), label, revision: integer(value.revision, 1),
    handedOverAt: nullableText(value.handedOverAt), deliveredAt: nullableText(value.deliveredAt),
    cancelledAt: nullableText(value.cancelledAt), createdAt: text(value.createdAt), updatedAt: text(value.updatedAt),
    events: value.events.map(parseTrackingEvent),
  }
}

async function csrf(): Promise<string> {
  const value = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(value) || typeof value.requestToken !== 'string') return invalid()
  return value.requestToken
}

async function jsonMutation(path: string, method: 'POST' | 'PATCH', body: unknown): Promise<unknown> {
  const token = await csrf()
  return apiRequest(path, { method, headers: { 'X-CSRF-TOKEN': token }, body: JSON.stringify(body) })
}

export async function listAdminCarriers(query = '', status = ''): Promise<CarrierSummary[]> {
  const params = new URLSearchParams()
  if (query.trim()) params.set('q', query.trim())
  if (status) params.set('status', status)
  const value = await apiRequest(`/admin/carriers${params.size ? `?${params}` : ''}`, { cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseCarrierSummary)
}
export async function getAdminCarrier(id: string): Promise<CarrierDetail> {
  return parseCarrierDetail(await apiRequest(`/admin/carriers/${encodeURIComponent(id)}`, { cache: 'no-store' }))
}
export async function createAdminCarrier(payload: CarrierPayload): Promise<CarrierDetail> {
  return parseCarrierDetail(await jsonMutation('/admin/carriers', 'POST', payload))
}
export async function updateAdminCarrier(id: string, payload: CarrierPayload): Promise<CarrierDetail> {
  return parseCarrierDetail(await jsonMutation(`/admin/carriers/${encodeURIComponent(id)}`, 'PATCH', payload))
}
export async function transitionAdminCarrier(id: string, action: 'activate' | 'deactivate', expectedRevision: number): Promise<CarrierDetail> {
  return parseCarrierDetail(await jsonMutation(`/admin/carriers/${encodeURIComponent(id)}/${action}`, 'POST', { expectedRevision }))
}

export async function listStaffActiveCarriers(): Promise<CarrierSummary[]> {
  const value = await apiRequest('/staff/carrier-consignments/carriers', { cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseCarrierSummary)
}
export async function listStaffConsignments(status = ''): Promise<CarrierConsignment[]> {
  const value = await apiRequest(`/staff/carrier-consignments/queue${status ? `?status=${encodeURIComponent(status)}` : ''}`, { cache: 'no-store' })
  if (!Array.isArray(value)) return invalid()
  return value.map(parseCarrierConsignment)
}
export async function getStaffConsignment(id: string): Promise<CarrierConsignment> {
  return parseCarrierConsignment(await apiRequest(`/staff/carrier-consignments/${encodeURIComponent(id)}`, { cache: 'no-store' }))
}
export async function createStaffConsignment(targetType: 'SALES' | 'EXCHANGE', shipmentId: string, payload: { carrierId: string; trackingNumber: string; serviceCode: string | null }): Promise<CarrierConsignment> {
  const segment = targetType === 'SALES' ? 'sales-shipments' : 'exchange-shipments'
  return parseCarrierConsignment(await jsonMutation(`/staff/carrier-consignments/${segment}/${encodeURIComponent(shipmentId)}`, 'POST', payload))
}
export async function uploadStaffCarrierLabel(id: string, file: File, expectedRevision: number): Promise<CarrierConsignment> {
  const token = await csrf()
  const form = new FormData(); form.set('file', file); form.set('expectedRevision', String(expectedRevision))
  return parseCarrierConsignment(await apiRequest(`/staff/carrier-consignments/${encodeURIComponent(id)}/label`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': token }, body: form,
  }))
}
export async function transitionStaffConsignment(id: string, action: 'handover' | 'cancel', expectedRevision: number): Promise<CarrierConsignment> {
  return parseCarrierConsignment(await jsonMutation(`/staff/carrier-consignments/${encodeURIComponent(id)}/${action}`, 'POST', { expectedRevision }))
}
export async function recordStaffTrackingEvent(id: string, payload: { eventKey: string; eventType: string; location: string | null; description: string | null; occurredAt: string; expectedRevision: number }): Promise<CarrierConsignment> {
  return parseCarrierConsignment(await jsonMutation(`/staff/carrier-consignments/${encodeURIComponent(id)}/events`, 'POST', payload))
}
export async function getCustomerOrderTracking(orderId: string): Promise<CarrierConsignment> {
  return parseCarrierConsignment(await apiRequest(`/orders/${encodeURIComponent(orderId)}/tracking`, { cache: 'no-store' }))
}

export function safeTrackingUrl(value: string | null): string | null {
  if (!value) return null
  try { const url = new URL(value); return url.protocol === 'https:' ? url.href : null } catch { return null }
}
