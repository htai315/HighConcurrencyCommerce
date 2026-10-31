import { redirect } from 'react-router-dom'
import type { ActionFunctionArgs, LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { passwordError } from './password-policy'
import { clearGuestCart, readGuestCart, setCartMergeNotice } from '../cart/guest-cart'

export type Role = 'CUSTOMER' | 'STAFF' | 'ADMIN'
export type AuthUser = { id: string; email: string; fullName: string; role: Role }

function parseUser(value: unknown): AuthUser {
  if (!isRecord(value) || typeof value.id !== 'string' || typeof value.email !== 'string' ||
      typeof value.fullName !== 'string' || (value.role !== 'CUSTOMER' && value.role !== 'STAFF' && value.role !== 'ADMIN'))
    throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return { id: value.id, email: value.email, fullName: value.fullName, role: value.role }
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

export async function authMutation(path: string, body: unknown, signal: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, { method: 'POST', body: JSON.stringify(body), signal, headers: { 'X-CSRF-TOKEN': csrf.requestToken } })
}

function value(form: FormData, key: string): string {
  const result = form.get(key)
  return typeof result === 'string' ? result : ''
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 429) return 'Bạn thao tác quá nhanh. Vui lòng đợi rồi thử lại.'
    if (error.code === 'CSRF_INVALID') return 'Phiên bảo vệ đã thay đổi. Vui lòng thử lại.'
    if (error.status === 401) return 'Email hoặc mật khẩu không đúng.'
    if (error.status === 400) return 'Thông tin chưa hợp lệ. Kiểm tra email, họ tên và mật khẩu.'
  }
  return 'Chưa thể thực hiện yêu cầu. Vui lòng thử lại.'
}

// Never accept protocol-relative, cross-origin or backslash-based redirects.
export function safeReturnTo(requestUrl: string): string {
  const request = new URL(requestUrl)
  const destination = request.searchParams.get('returnTo') ?? '/'
  if (!destination.startsWith('/') || destination.startsWith('//') || /[\\\r\n]/.test(destination)) return '/'
  const parsed = new URL(destination, request.origin)
  return parsed.origin === request.origin ? parsed.pathname + parsed.search + parsed.hash : '/'
}

export async function loginAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  let user: AuthUser
  try {
    user = parseUser(await authMutation('/auth/login', { email: value(form, 'email'), password: value(form, 'password') }, request.signal))
  } catch (error) { return { error: errorMessage(error) } }
  if (user.role === 'CUSTOMER') {
    const guestItems = readGuestCart()
    if (guestItems.length > 0) {
      try {
        const result = await authMutation('/cart/merge', {
          items: guestItems.map(item => ({ productSkuId: item.productSkuId, quantity: item.quantity, flashSaleOfferId: item.flashSaleOfferId ?? null })),
        }, request.signal)
        const skipped = isRecord(result) && Array.isArray(result.issues)
          ? result.issues.filter(issue => isRecord(issue) && issue.code === 'SKU_NOT_AVAILABLE').length : 0
        clearGuestCart()
        setCartMergeNotice(skipped > 0
          ? `Đã gộp giỏ Guest; bỏ qua ${skipped} sản phẩm không còn được bán.`
          : 'Đã gộp giỏ Guest vào tài khoản của bạn.')
      } catch { /* Login remains successful; local cart is retained for a later retry. */ }
    }
  }
  throw redirect(safeReturnTo(request.url))
}

export async function registerAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  const password = value(form, 'password')
  const confirmPassword = value(form, 'confirmPassword')
  const error = passwordError(password)
  if (error) return { error }
  if (password !== confirmPassword) return { error: 'Mật khẩu nhập lại chưa khớp.' }
  try {
    await authMutation('/auth/register', { email: value(form, 'email'), password, confirmPassword, fullName: value(form, 'fullName') }, request.signal)
  } catch (error) { return { error: errorMessage(error) } }
  throw redirect('/login?registered=1')
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

