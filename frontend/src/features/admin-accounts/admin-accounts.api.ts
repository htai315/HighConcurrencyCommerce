import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type Role = 'CUSTOMER' | 'STAFF' | 'ADMIN'
export type AccountStatus = 'ACTIVE' | 'LOCKED'
export type LifecycleStatus = 'INVITATION_PENDING' | 'ACTIVE' | 'LOCKED'

export interface AdminAccountListItem {
  id: string
  email: string
  fullName: string
  role: Role
  accountStatus: AccountStatus
  lifecycleStatus: LifecycleStatus
  emailVerifiedAt: string | null
  createdAt: string
  activeSessionCount: number
  latestSessionAt: string | null
}

export interface AdminAccountDetail {
  id: string
  email: string
  fullName: string
  phone: string | null
  role: Role
  accountStatus: AccountStatus
  lifecycleStatus: LifecycleStatus
  emailVerifiedAt: string | null
  lockedAt: string | null
  createdAt: string
  updatedAt: string
  activeSessionCount: number
  latestSessionAt: string | null
  latestSessionExpiresAt: string | null
  hasPassword: boolean
  hasGoogleIdentity: boolean
}

export interface AdminAccountPage {
  items: AdminAccountListItem[]
  page: number
  pageSize: number
  totalCount: number
}

export interface AdminAccountQuery {
  q?: string
  role?: string
  status?: string
  lifecycle?: string
  page?: number
  pageSize?: number
}

export interface InviteStaffPayload {
  email: string
  fullName: string
}

export interface AcceptStaffInvitationPayload {
  token: string
  password: string
  confirmPassword: string
}

async function getCsrfToken(signal?: AbortSignal): Promise<string> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') {
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  }
  return csrf.requestToken
}

function invalidResponse(): never {
  throw new ApiError(502, { code: 'INVALID_RESPONSE' })
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : invalidResponse()
}

function nullableText(value: unknown): string | null {
  return value === null ? null : text(value)
}

function nonNegativeInteger(value: unknown): number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
    ? value
    : invalidResponse()
}

function positiveInteger(value: unknown): number {
  const parsed = nonNegativeInteger(value)
  return parsed > 0 ? parsed : invalidResponse()
}

function role(value: unknown): Role {
  return value === 'CUSTOMER' || value === 'STAFF' || value === 'ADMIN'
    ? value
    : invalidResponse()
}

function accountStatus(value: unknown): AccountStatus {
  return value === 'ACTIVE' || value === 'LOCKED' ? value : invalidResponse()
}

function lifecycleStatus(value: unknown): LifecycleStatus {
  return value === 'INVITATION_PENDING' || value === 'ACTIVE' || value === 'LOCKED'
    ? value
    : invalidResponse()
}

function parseListItem(value: unknown): AdminAccountListItem {
  if (!isRecord(value)) return invalidResponse()
  return {
    id: text(value.id),
    email: text(value.email),
    fullName: text(value.fullName),
    role: role(value.role),
    accountStatus: accountStatus(value.accountStatus),
    lifecycleStatus: lifecycleStatus(value.lifecycleStatus),
    emailVerifiedAt: nullableText(value.emailVerifiedAt),
    createdAt: text(value.createdAt),
    activeSessionCount: nonNegativeInteger(value.activeSessionCount),
    latestSessionAt: nullableText(value.latestSessionAt),
  }
}

function parseDetail(value: unknown): AdminAccountDetail {
  if (!isRecord(value) || typeof value.hasPassword !== 'boolean' || typeof value.hasGoogleIdentity !== 'boolean')
    return invalidResponse()
  const base = parseListItem(value)
  return {
    ...base,
    phone: nullableText(value.phone),
    lockedAt: nullableText(value.lockedAt),
    updatedAt: text(value.updatedAt),
    latestSessionExpiresAt: nullableText(value.latestSessionExpiresAt),
    hasPassword: value.hasPassword,
    hasGoogleIdentity: value.hasGoogleIdentity,
  }
}

export function adminAccountErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'CSRF_INVALID') return 'Phiên bảo vệ đã thay đổi. Vui lòng thử lại.'
    if (error.code === 'EMAIL_ALREADY_EXISTS') return 'Email này đã tồn tại trong hệ thống.'
    if (error.code === 'SELF_STATUS_CHANGE') return 'Không thể thay đổi trạng thái tài khoản của chính mình.'
    if (error.code === 'SELF_REVOKE_PROHIBITED') return 'Không thể thu hồi phiên đăng nhập của chính mình.'
    if (error.code === 'FORBIDDEN') return 'Bạn không có quyền thực hiện thao tác này.'
    if (error.code === 'NOT_FOUND') return 'Không tìm thấy tài khoản.'
    if (error.code === 'RATE_LIMITED') return 'Thao tác quá nhanh. Vui lòng đợi trong giây lát.'
    if (error.code === 'ACCOUNT_LOCKED') return 'Tài khoản hiện đang bị khóa.'
    if (error.code === 'INVALID_OR_EXPIRED_TOKEN') return 'Liên kết mời không hợp lệ hoặc đã hết hạn.'
    if (error.message) return error.message
  }
  return error instanceof Error ? error.message : 'Đã có lỗi xảy ra. Vui lòng thử lại.'
}

export async function getAdminAccounts(query?: AdminAccountQuery, signal?: AbortSignal): Promise<AdminAccountPage> {
  const params = new URLSearchParams()
  if (query?.q) params.set('q', query.q)
  if (query?.role) params.set('role', query.role)
  if (query?.status) params.set('status', query.status)
  if (query?.lifecycle) params.set('lifecycle', query.lifecycle)
  if (query?.page) params.set('page', query.page.toString())
  if (query?.pageSize) params.set('pageSize', query.pageSize.toString())

  const queryString = params.toString()
  const path = queryString ? `/admin/accounts?${queryString}` : '/admin/accounts'

  const res = await apiRequest(path, { signal, cache: 'no-store' })
  if (!isRecord(res) || !Array.isArray(res.items)) return invalidResponse()
  return {
    items: res.items.map(parseListItem),
    page: positiveInteger(res.page),
    pageSize: positiveInteger(res.pageSize),
    totalCount: nonNegativeInteger(res.totalCount),
  }
}

export async function getAdminAccountById(id: string, signal?: AbortSignal): Promise<AdminAccountDetail> {
  const res = await apiRequest(`/admin/accounts/${encodeURIComponent(id)}`, { signal, cache: 'no-store' })
  return parseDetail(res)
}

export async function inviteStaff(payload: InviteStaffPayload, signal?: AbortSignal): Promise<AdminAccountDetail> {
  const csrf = await getCsrfToken(signal)
  const res = await apiRequest('/admin/staff-invitations', {
    method: 'POST',
    body: JSON.stringify(payload),
    headers: { 'X-CSRF-TOKEN': csrf },
    signal,
  })
  return parseDetail(res)
}

export async function resendStaffInvitation(id: string, signal?: AbortSignal): Promise<void> {
  const csrf = await getCsrfToken(signal)
  await apiRequest(`/admin/accounts/${encodeURIComponent(id)}/staff-invitation/resend`, {
    method: 'POST',
    body: JSON.stringify({}),
    headers: { 'X-CSRF-TOKEN': csrf },
    signal,
  })
}

export async function setAccountStatus(id: string, status: AccountStatus, signal?: AbortSignal): Promise<void> {
  const csrf = await getCsrfToken(signal)
  await apiRequest(`/admin/accounts/${encodeURIComponent(id)}/status`, {
    method: 'PATCH',
    body: JSON.stringify({ status }),
    headers: { 'X-CSRF-TOKEN': csrf },
    signal,
  })
}

export async function revokeAccountSessions(id: string, signal?: AbortSignal): Promise<void> {
  const csrf = await getCsrfToken(signal)
  await apiRequest(`/admin/accounts/${encodeURIComponent(id)}/sessions/revoke`, {
    method: 'POST',
    body: JSON.stringify({}),
    headers: { 'X-CSRF-TOKEN': csrf },
    signal,
  })
}

export async function acceptStaffInvitation(payload: AcceptStaffInvitationPayload, signal?: AbortSignal): Promise<void> {
  const csrf = await getCsrfToken(signal)
  await apiRequest('/auth/staff-invitation/accept', {
    method: 'POST',
    body: JSON.stringify(payload),
    headers: { 'X-CSRF-TOKEN': csrf },
    signal,
  })
}

