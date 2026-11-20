import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type DiscountType = 'FIXED_AMOUNT' | 'PERCENTAGE'
export type VoucherStatus = 'DRAFT' | 'ACTIVE' | 'PAUSED' | 'ENDED'

export interface VoucherPreview {
  code: string
  subtotalAmount: number
  discountAmount: number
  totalAmount: number
  validUntil: string
}

export interface AdminVoucherSummary {
  id: string
  code: string
  name: string
  discountType: DiscountType
  discountValue: number
  voucherStatus: VoucherStatus
  minimumOrderAmount: number
  maxDiscountAmount: number | null
  totalUsageLimit?: number | null
  usageLimitTotal?: number | null
  perCustomerLimit?: number
  usageLimitPerCustomer?: number
  usageHeld: number
  usageConsumed: number
  budgetTotal?: number | null
  totalBudgetAmount?: number | null
  budgetHeld: number
  budgetConsumed: number
  startsAt: string
  endsAt: string
  createdAt: string
  updatedAt: string
}

export interface AdminVoucherPage {
  items: AdminVoucherSummary[]
  page: number
  pageSize: number
  totalCount: number
}

export interface CreateVoucherPayload {
  code: string
  name?: string
  discountType: DiscountType
  discountValue: number
  minimumOrderAmount: number
  maxDiscountAmount?: number | null
  totalUsageLimit?: number | null
  usageLimitTotal?: number | null
  perCustomerLimit?: number
  usageLimitPerCustomer?: number
  budgetTotal?: number | null
  totalBudgetAmount?: number | null
  startsAt: string
  endsAt: string
}

export interface UpdateVoucherPayload {
  code: string
  name?: string
  discountType: DiscountType
  discountValue: number
  minimumOrderAmount: number
  maxDiscountAmount?: number | null
  totalUsageLimit?: number | null
  usageLimitTotal?: number | null
  perCustomerLimit?: number
  usageLimitPerCustomer?: number
  budgetTotal?: number | null
  totalBudgetAmount?: number | null
  startsAt: string
  endsAt: string
}

function invalid(): never {
  throw new ApiError(502, { code: 'INVALID_RESPONSE' })
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : invalid()
}

function amount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : invalid()
}

function integer(value: unknown, min = 0): number {
  const parsed = amount(value)
  return Number.isSafeInteger(parsed) && parsed >= min ? parsed : invalid()
}

function nullableAmount(value: unknown): number | null {
  return value === null || value === undefined ? null : amount(value)
}

export function parseVoucherPreview(value: unknown): VoucherPreview {
  if (!isRecord(value)) return invalid()
  return {
    code: text(value.code),
    subtotalAmount: amount(value.subtotalAmount),
    discountAmount: amount(value.discountAmount),
    totalAmount: amount(value.totalAmount),
    validUntil: text(value.validUntil),
  }
}

export function parseAdminVoucherSummary(value: unknown): AdminVoucherSummary {
  if (!isRecord(value)) return invalid()
  const discountType = text(value.discountType)
  if (discountType !== 'FIXED_AMOUNT' && discountType !== 'PERCENTAGE') return invalid()

  const voucherStatus = text(value.voucherStatus)
  if (voucherStatus !== 'DRAFT' && voucherStatus !== 'ACTIVE' && voucherStatus !== 'PAUSED' && voucherStatus !== 'ENDED') {
    return invalid()
  }

  const totalLimit = integer(value.totalUsageLimit ?? value.usageLimitTotal, 1)
  const perCust = integer(value.perCustomerLimit ?? value.usageLimitPerCustomer, 1)
  const budget = amount(value.budgetTotal ?? value.totalBudgetAmount)

  return {
    id: text(value.id),
    code: text(value.code),
    name: typeof value.name === 'string' ? value.name : '',
    discountType,
    discountValue: amount(value.discountValue),
    voucherStatus,
    minimumOrderAmount: amount(value.minimumOrderAmount),
    maxDiscountAmount: nullableAmount(value.maxDiscountAmount),
    totalUsageLimit: totalLimit,
    usageLimitTotal: totalLimit,
    perCustomerLimit: perCust,
    usageLimitPerCustomer: perCust,
    usageHeld: integer(value.usageHeld, 0),
    usageConsumed: integer(value.usageConsumed, 0),
    budgetTotal: budget,
    totalBudgetAmount: budget,
    budgetHeld: amount(value.budgetHeld),
    budgetConsumed: amount(value.budgetConsumed),
    startsAt: text(value.startsAt),
    endsAt: text(value.endsAt),
    createdAt: text(value.createdAt),
    updatedAt: text(value.updatedAt),
  }
}

export function parseAdminVoucherPage(value: unknown): AdminVoucherPage {
  if (!isRecord(value) || !Array.isArray(value.items)) return invalid()
  return {
    items: value.items.map(parseAdminVoucherSummary),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount, 0),
  }
}

async function getCsrfToken(): Promise<string> {
  const csrf = await apiRequest('/auth/csrf', { cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') return invalid()
  return csrf.requestToken
}

export async function previewVoucher(code: string): Promise<VoucherPreview> {
  const token = await getCsrfToken()
  const normalized = code.trim().toUpperCase()
  const res = await apiRequest('/vouchers/preview', {
    method: 'POST',
    body: JSON.stringify({ code: normalized }),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseVoucherPreview(res)
}

export async function listAdminVouchers(params?: {
  page?: number
  pageSize?: number
  status?: string
  q?: string
}): Promise<AdminVoucherPage> {
  const query = new URLSearchParams()
  if (params?.page) query.set('page', String(params.page))
  if (params?.pageSize) query.set('pageSize', String(params.pageSize))
  if (params?.status && params.status !== 'ALL') query.set('status', params.status)
  if (params?.q) query.set('q', params.q)

  const qs = query.toString()
  const res = await apiRequest(`/admin/vouchers${qs ? `?${qs}` : ''}`, { cache: 'no-store' })
  return parseAdminVoucherPage(res)
}

export async function getAdminVoucher(id: string): Promise<AdminVoucherSummary> {
  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}`, { cache: 'no-store' })
  return parseAdminVoucherSummary(res)
}

export async function createAdminVoucher(payload: CreateVoucherPayload): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const totalUsageLimit = payload.totalUsageLimit ?? payload.usageLimitTotal ?? 1000
  const perCustomerLimit = payload.perCustomerLimit ?? payload.usageLimitPerCustomer ?? 1
  const budgetTotal = payload.budgetTotal ?? payload.totalBudgetAmount ?? (totalUsageLimit * (payload.discountType === 'FIXED_AMOUNT' ? payload.discountValue : (payload.maxDiscountAmount ?? 500000)))

  const body = {
    code: payload.code.trim().toUpperCase(),
    name: payload.name ? payload.name.trim() : payload.code.trim().toUpperCase(),
    discountType: payload.discountType,
    discountValue: payload.discountValue,
    minimumOrderAmount: payload.minimumOrderAmount,
    maxDiscountAmount: payload.maxDiscountAmount ?? null,
    totalUsageLimit,
    perCustomerLimit,
    budgetTotal,
    startsAt: payload.startsAt,
    endsAt: payload.endsAt,
  }

  const res = await apiRequest('/admin/vouchers', {
    method: 'POST',
    body: JSON.stringify(body),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export async function updateAdminVoucher(id: string, payload: UpdateVoucherPayload): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const totalUsageLimit = payload.totalUsageLimit ?? payload.usageLimitTotal ?? 1000
  const perCustomerLimit = payload.perCustomerLimit ?? payload.usageLimitPerCustomer ?? 1
  const budgetTotal = payload.budgetTotal ?? payload.totalBudgetAmount ?? (totalUsageLimit * (payload.discountType === 'FIXED_AMOUNT' ? payload.discountValue : (payload.maxDiscountAmount ?? 500000)))

  const body = {
    code: payload.code.trim().toUpperCase(),
    name: payload.name ? payload.name.trim() : payload.code.trim().toUpperCase(),
    discountType: payload.discountType,
    discountValue: payload.discountValue,
    minimumOrderAmount: payload.minimumOrderAmount,
    maxDiscountAmount: payload.maxDiscountAmount ?? null,
    totalUsageLimit,
    perCustomerLimit,
    budgetTotal,
    startsAt: payload.startsAt,
    endsAt: payload.endsAt,
  }

  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify(body),
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export async function activateAdminVoucher(id: string): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}/activate`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export async function pauseAdminVoucher(id: string): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}/pause`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export async function resumeAdminVoucher(id: string): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}/resume`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export async function endAdminVoucher(id: string): Promise<AdminVoucherSummary> {
  const token = await getCsrfToken()
  const res = await apiRequest(`/admin/vouchers/${encodeURIComponent(id)}/end`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  })
  return parseAdminVoucherSummary(res)
}

export const VOUCHER_ERROR_MESSAGES: Record<string, string> = {
  VOUCHER_NOT_FOUND: 'Mã giảm giá không tồn tại hoặc đã bị xóa.',
  VOUCHER_NOT_ACTIVE: 'Mã giảm giá hiện chưa mở hoặc đang tạm dừng.',
  VOUCHER_NOT_STARTED: 'Mã giảm giá chưa đến thời gian áp dụng.',
  VOUCHER_EXPIRED: 'Mã giảm giá đã hết hạn sử dụng.',
  VOUCHER_MINIMUM_NOT_MET: 'Đơn hàng chưa đạt giá trị tối thiểu để áp dụng mã giảm giá này.',
  VOUCHER_USAGE_LIMIT_REACHED: 'Mã giảm giá đã hết lượt sử dụng.',
  VOUCHER_CUSTOMER_LIMIT_REACHED: 'Bạn đã sử dụng hết lượt cho mã giảm giá này.',
  VOUCHER_BUDGET_EXHAUSTED: 'Ngân sách ưu đãi của mã giảm giá này đã hết.',
  VOUCHER_DISCOUNT_INVALID: 'Mức giảm giá của mã không hợp lệ với đơn hàng này.',
  EMPTY_CART: 'Giỏ hàng đang trống, không thể áp dụng mã giảm giá.',
}
