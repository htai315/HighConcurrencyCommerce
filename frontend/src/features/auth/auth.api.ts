import { redirect } from 'react-router-dom'
import type { ActionFunctionArgs, LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { passwordError } from './password-policy'
import { clearPendingMergeSnapshot, commitPendingMerge, createPendingMergeSnapshot, readGuestCart, readPendingMergeSnapshot, setCartMergeNotice } from '../cart/guest-cart'

export type Role = 'CUSTOMER' | 'STAFF' | 'ADMIN'
export type AuthUser = { id: string; email: string; fullName: string; role: Role; emailVerified: boolean }

export type AuthProviderInfo = { enabled: boolean; clientId?: string }
export type AuthProvidersResponse = { google: AuthProviderInfo }
export type AuthMethodsResponse = { hasPassword: boolean; googleLinked: boolean; googleEmail?: string }
export type GoogleChallengeResponse = { challengeId: string; nonce: string }

function parseUser(value: unknown): AuthUser {
  if (!isRecord(value) || typeof value.id !== 'string' || typeof value.email !== 'string' ||
      typeof value.fullName !== 'string' || (value.role !== 'CUSTOMER' && value.role !== 'STAFF' && value.role !== 'ADMIN'))
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return {
    id: value.id,
    email: value.email,
    fullName: value.fullName,
    role: value.role,
    // Fail closed at the client trust boundary. A malformed/legacy response
    // must never make an unverified account appear verified in the UI.
    emailVerified: typeof value.emailVerified === 'boolean' ? value.emailVerified : false,
  }
}

export async function sessionLoader({ request }: LoaderFunctionArgs): Promise<AuthUser | null> {
  try { return parseUser(await apiRequest('/auth/me', { signal: request.signal, cache: 'no-store' })) }
  catch (error) {
    if (error instanceof ApiError && error.status === 401) return null
    throw error
  }
}

export function requireRole(roles: readonly Role[]) {
  return async (args: LoaderFunctionArgs) => {
    const user = await sessionLoader(args)
    const url = new URL(args.request.url)
    if (!user) throw redirect('/login?returnTo=' + encodeURIComponent(url.pathname + url.search))
    if (!roles.includes(user.role)) throw new ApiError(403, { code: 'FORBIDDEN' })
    return user
  }
}

export async function authMutation(
  path: string,
  body: unknown,
  signal?: AbortSignal,
  method: 'POST' | 'DELETE' = 'POST'
): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: JSON.stringify(body),
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken }
  })
}

function value(form: FormData, key: string): string {
  const result = form.get(key)
  return typeof result === 'string' ? result : ''
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 429) return 'Bạn thao tác quá nhanh. Vui lòng đợi rồi thử lại.'
    if (error.code === 'CSRF_INVALID') return 'Phiên bảo vệ đã thay đổi. Vui lòng thử lại.'
    if (error.code === 'ACCOUNT_LINK_REQUIRED')
      return 'Email này đã được đăng ký bằng mật khẩu. Vui lòng đăng nhập bằng mật khẩu rồi liên kết Google trong mục Bảo mật tài khoản.'
    if (error.code === 'IDENTITY_ALREADY_LINKED')
      return 'Tài khoản Google này đã được liên kết với một tài khoản khác.'
    if (error.code === 'LAST_AUTH_METHOD')
      return 'Không thể hủy liên kết Google khi tài khoản chưa cài đặt mật khẩu đăng nhập.'
    if (error.code === 'EMAIL_MISMATCH')
      return 'Email của tài khoản Google phải trùng khớp với email của tài khoản đang đăng nhập.'
    if (error.code === 'GOOGLE_AUTH_INVALID')
      return 'Thông tin xác thực Google không hợp lệ hoặc đã hết hạn.'
    if (error.code === 'GOOGLE_AUTH_UNAVAILABLE')
      return 'Không thể kết nối đến máy chủ Google. Vui lòng thử lại sau.'
    if (error.code === 'INVALID_CHALLENGE')
      return 'Phiên bảo vệ xác thực không hợp lệ hoặc đã hết hạn. Vui lòng thử lại.'
    if (error.code === 'SESSION_CONFLICT')
      return 'Bạn đang đăng nhập bằng một tài khoản khác. Vui lòng đăng xuất trước khi đăng nhập tài khoản này.'
    if (error.code === 'EMAIL_NOT_VERIFIED')
      return 'Email chưa được xác minh. Vui lòng kiểm tra hộp thư hoặc yêu cầu gửi lại email xác minh.'
    if (error.code === 'INVALID_OR_EXPIRED_TOKEN')
      return 'Liên kết không hợp lệ hoặc đã hết hạn.'
    if (error.code === 'PASSWORD_RESET_SAME_AS_CURRENT')
      return 'Mật khẩu mới không được trùng với mật khẩu hiện tại.'
    if (error.status === 401) return 'Email hoặc mật khẩu không đúng.'
    if (error.status === 400) return 'Thông tin chưa hợp lệ. Kiểm tra email, họ tên và mật khẩu.'
  }
  return 'Chưa thể thực hiện yêu cầu. Vui lòng thử lại.'
}

// Never accept protocol-relative, cross-origin or backslash-based redirects.
export function safeReturnTo(requestUrl: string): string {
  const request = new URL(requestUrl, window.location.origin)
  const destination = request.searchParams.get('returnTo') ?? '/'
  if (!destination.startsWith('/') || destination.startsWith('//') || /[\\\r\n]/.test(destination)) return '/'
  const parsed = new URL(destination, request.origin)
  return parsed.origin === request.origin ? parsed.pathname + parsed.search + parsed.hash : '/'
}

const CART_MERGE_LOCK = 'hcc:guest-cart-merge'
let fallbackCartMergeQueue: Promise<void> = Promise.resolve()

async function withCartMergeLock<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof navigator !== 'undefined' && navigator.locks) {
    return navigator.locks.request(CART_MERGE_LOCK, operation)
  }

  const previous = fallbackCartMergeQueue
  let release!: () => void
  fallbackCartMergeQueue = new Promise<void>(resolve => { release = resolve })
  await previous
  try {
    return await operation()
  } finally {
    release()
  }
}

async function mergeGuestCartAfterSignIn(signal?: AbortSignal): Promise<void> {
  await withCartMergeLock(async () => {
    let totalSkipped = 0
    let hasMerged = false

    while (true) {
      let snapshot = readPendingMergeSnapshot()
      if (!snapshot) {
        const guestItems = readGuestCart()
        if (guestItems.length === 0) break
        snapshot = createPendingMergeSnapshot(guestItems)
      }

      try {
        const result = await authMutation('/cart/merge', {
          mergeBatchId: snapshot.batchId,
          items: snapshot.items.map(item => ({
            productSkuId: item.productSkuId,
            quantity: item.quantity,
            flashSaleOfferId: item.flashSaleOfferId ?? null
          })),
        }, signal)

        const skipped = isRecord(result) && Array.isArray(result.issues)
          ? result.issues.filter(issue => isRecord(issue) && issue.code === 'SKU_NOT_AVAILABLE').length : 0
        totalSkipped += skipped
        hasMerged = true

        commitPendingMerge(snapshot)

        const remaining = readGuestCart()
        if (remaining.length === 0) break
      } catch (err) {
        if (err instanceof ApiError && err.code === 'IDEMPOTENCY_KEY_REUSED') {
          clearPendingMergeSnapshot()
        }
        /* Login remains successful; pending snapshot or local cart is retained for later retry. */
        break
      }
    }

    if (hasMerged) {
      setCartMergeNotice(totalSkipped > 0
        ? `Đã gộp giỏ Guest; bỏ qua ${totalSkipped} sản phẩm không còn được bán.`
        : 'Đã gộp giỏ Guest vào tài khoản của bạn.')
    }
  })
}

export async function finishSignIn(user: AuthUser, signal?: AbortSignal): Promise<void> {
  if (user.role === 'CUSTOMER') {
    await mergeGuestCartAfterSignIn(signal)
  }
}

export async function loginAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  const email = value(form, 'email')
  let user: AuthUser
  try {
    user = parseUser(await authMutation('/auth/login', { email, password: value(form, 'password') }, request.signal))
  } catch (error) {
    if (error instanceof ApiError && error.code === 'EMAIL_NOT_VERIFIED') {
      return { error: errorMessage(error), code: 'EMAIL_NOT_VERIFIED', email }
    }
    return { error: errorMessage(error) }
  }
  await finishSignIn(user, request.signal)
  throw redirect(safeReturnTo(request.url))
}

export async function getAuthProviders(signal?: AbortSignal): Promise<AuthProvidersResponse> {
  const response = await apiRequest('/auth/providers', { signal, cache: 'no-store' })
  if (!isRecord(response) || !isRecord(response.google) || typeof response.google.enabled !== 'boolean')
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return {
    google: {
      enabled: response.google.enabled,
      clientId: typeof response.google.clientId === 'string' ? response.google.clientId : undefined
    }
  }
}

export async function createGoogleChallenge(purpose: 'LOGIN' | 'LINK', signal?: AbortSignal): Promise<GoogleChallengeResponse> {
  const result = await authMutation('/auth/google/challenge', { purpose }, signal)
  if (!isRecord(result) || typeof result.challengeId !== 'string' || typeof result.nonce !== 'string')
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return { challengeId: result.challengeId, nonce: result.nonce }
}

export async function googleLogin(credential: string, challengeId: string, signal?: AbortSignal): Promise<AuthUser> {
  const result = await authMutation('/auth/google', { credential, challengeId }, signal)
  return parseUser(result)
}

export async function getAuthMethods(signal?: AbortSignal): Promise<AuthMethodsResponse> {
  const response = await apiRequest('/account/auth-methods', { signal, cache: 'no-store' })
  if (!isRecord(response) || typeof response.hasPassword !== 'boolean' || typeof response.googleLinked !== 'boolean')
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return {
    hasPassword: response.hasPassword,
    googleLinked: response.googleLinked,
    googleEmail: typeof response.googleEmail === 'string' ? response.googleEmail : undefined
  }
}

export async function linkGoogle(credential: string, challengeId: string, currentPassword?: string, signal?: AbortSignal): Promise<void> {
  await authMutation('/account/auth-methods/google', { credential, challengeId, currentPassword: currentPassword || null }, signal)
}

export async function unlinkGoogle(currentPassword: string, signal?: AbortSignal): Promise<void> {
  await authMutation('/account/auth-methods/google', { currentPassword }, signal, 'DELETE')
}

export const PENDING_VERIFICATION_EMAIL_KEY = 'hcc:pending-verification-email'

export function setPendingVerificationEmail(email: string): void {
  try {
    sessionStorage.setItem(PENDING_VERIFICATION_EMAIL_KEY, email)
  } catch {}
}

export function getPendingVerificationEmail(): string {
  try {
    return sessionStorage.getItem(PENDING_VERIFICATION_EMAIL_KEY) || ''
  } catch {
    return ''
  }
}

export async function registerAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  const email = value(form, 'email')
  const password = value(form, 'password')
  const confirmPassword = value(form, 'confirmPassword')
  const error = passwordError(password)
  if (error) return { error }
  if (password !== confirmPassword) return { error: 'Mật khẩu nhập lại chưa khớp.' }
  try {
    await authMutation('/auth/register', { email, password, confirmPassword, fullName: value(form, 'fullName') }, request.signal)
  } catch (error) { return { error: errorMessage(error) } }
  setPendingVerificationEmail(email)
  throw redirect('/verify-email?registered=1')
}

export async function requestPasswordReset(email: string, signal?: AbortSignal): Promise<{ message: string }> {
  const res = await authMutation('/auth/password-reset/request', { email }, signal)
  if (isRecord(res) && typeof res.message === 'string') return { message: res.message }
  return { message: 'Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.' }
}

export async function confirmPasswordReset(payload: { token: string; newPassword: string; confirmPassword: string }, signal?: AbortSignal): Promise<void> {
  await authMutation('/auth/password-reset/confirm', payload, signal)
}

export async function resendVerificationEmail(email: string, signal?: AbortSignal): Promise<{ message: string }> {
  const res = await authMutation('/auth/email-verification/resend', { email }, signal)
  if (isRecord(res) && typeof res.message === 'string') return { message: res.message }
  return { message: 'Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.' }
}

export async function confirmEmailVerification(token: string, signal?: AbortSignal): Promise<void> {
  await authMutation('/auth/email-verification/confirm', { token }, signal)
}

export async function logoutAction({ request }: ActionFunctionArgs) {
  try { await authMutation('/auth/logout', {}, request.signal) }
  catch (error) {
    if (!(error instanceof ApiError && error.status === 401)) throw error
  }
  throw redirect('/')
}

export async function passwordAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  const error = passwordError(value(form, 'newPassword'))
  if (error) return { error }
  try {
    await authMutation('/auth/password', { currentPassword: value(form, 'currentPassword'), newPassword: value(form, 'newPassword') }, request.signal)
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) throw redirect('/login')
    return { error: error instanceof ApiError && error.code === 'INVALID_PASSWORD'
      ? 'Mật khẩu hiện tại không đúng hoặc mật khẩu mới trùng mật khẩu cũ.' : errorMessage(error) }
  }
  throw redirect('/login?passwordChanged=1')
}

