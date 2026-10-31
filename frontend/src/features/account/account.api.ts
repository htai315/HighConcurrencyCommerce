import type { ActionFunctionArgs, LoaderFunctionArgs } from 'react-router-dom'
import { ApiError, apiRequest, isRecord } from '../../lib/api/client'
import { requireRole } from '../auth/auth.api'

export type CustomerProfile = {
  id: string
  email: string
  fullName: string
  phone: string | null
  role: string
}

export type CustomerAddress = {
  id: string
  recipientName: string
  phone: string
  addressLine: string
  ward: string | null
  province: string
  isDefault: boolean
  createdAt: string
}

function invalid(): never { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function boolean(value: unknown): boolean { return typeof value === 'boolean' ? value : invalid() }
function list<T>(value: unknown, parse: (item: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }

export function parseProfile(value: unknown): CustomerProfile {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    email: string(value.email),
    fullName: string(value.fullName),
    phone: value.phone === null || value.phone === undefined ? null : string(value.phone),
    role: string(value.role),
  }
}

export function parseAddress(value: unknown): CustomerAddress {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    recipientName: string(value.recipientName),
    phone: string(value.phone),
    addressLine: string(value.addressLine),
    ward: value.ward === null || value.ward === undefined ? null : string(value.ward),
    province: string(value.province),
    isDefault: boolean(value.isDefault),
    createdAt: string(value.createdAt),
  }
}

export async function accountMutation(path: string, method: 'POST' | 'PATCH' | 'DELETE', body: unknown, signal: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
  })
}

export async function accountLoader(args: LoaderFunctionArgs) {
  await requireRole(['CUSTOMER'])(args)
  const [profile, addresses] = await Promise.all([
    apiRequest('/account/profile', { signal: args.request.signal, cache: 'no-store' }).then(parseProfile),
    apiRequest('/account/addresses', { signal: args.request.signal, cache: 'no-store' }).then(val => list(val, parseAddress)),
  ])
  return { profile, addresses }
}

function value(form: FormData, key: string): string {
  const result = form.get(key)
  return typeof result === 'string' ? result : ''
}

export async function accountAction({ request }: ActionFunctionArgs) {
  const form = await request.formData()
  const intent = value(form, 'intent')

  try {
    if (intent === 'update-profile') {
      const fullName = value(form, 'fullName').trim()
      const phone = value(form, 'phone').trim() || null
      if (!fullName) return { intent, error: 'Họ và tên không được để trống.' }
      await accountMutation('/account/profile', 'PATCH', { fullName, phone }, request.signal)
      return { intent, success: true, message: 'Cập nhật hồ sơ thành công.' }
    }

    if (intent === 'create-address') {
      const recipientName = value(form, 'recipientName').trim()
      const phone = value(form, 'phone').trim()
      const addressLine = value(form, 'addressLine').trim()
      const ward = value(form, 'ward').trim() || null
      const province = value(form, 'province').trim()
      const isDefault = form.get('isDefault') === 'true' || form.get('isDefault') === 'on'

      if (!recipientName || !phone || !addressLine || !province) {
        return { intent, error: 'Vui lòng điền đầy đủ các thông tin bắt buộc.' }
      }

      await accountMutation('/account/addresses', 'POST', { recipientName, phone, addressLine, ward, province, isDefault }, request.signal)
      return { intent, success: true, message: 'Thêm địa chỉ mới thành công.' }
    }

    if (intent === 'update-address') {
      const id = value(form, 'id')
      if (!id) return { intent, error: 'Thiếu ID địa chỉ.' }
      const recipientName = value(form, 'recipientName').trim()
      const phone = value(form, 'phone').trim()
      const addressLine = value(form, 'addressLine').trim()
      const ward = value(form, 'ward').trim() || null
      const province = value(form, 'province').trim()
      const isDefault = form.get('isDefault') === 'true' || form.get('isDefault') === 'on'

      if (!recipientName || !phone || !addressLine || !province) {
        return { intent, error: 'Vui lòng điền đầy đủ các thông tin bắt buộc.' }
      }

      await accountMutation(`/account/addresses/${encodeURIComponent(id)}`, 'PATCH', { recipientName, phone, addressLine, ward, province, isDefault }, request.signal)
      return { intent, success: true, message: 'Cập nhật địa chỉ thành công.' }
    }

    if (intent === 'delete-address') {
      const id = value(form, 'id')
      if (!id) return { intent, error: 'Thiếu ID địa chỉ.' }
      await accountMutation(`/account/addresses/${encodeURIComponent(id)}`, 'DELETE', undefined, request.signal)
      return { intent, success: true, message: 'Đã xóa địa chỉ.' }
    }

    return { error: 'Thao tác không hợp lệ.' }
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 409 && error.code === 'ADDRESS_IN_USE') {
        return { intent, error: 'Địa chỉ này đã được sử dụng trong đơn hàng, không thể xóa.' }
      }
      if (error.status === 400) {
        return { intent, error: 'Dữ liệu không hợp lệ. Vui lòng kiểm tra lại.' }
      }
      if (error.status === 404) {
        return { intent, error: 'Không tìm thấy thông tin.' }
      }
    }
    return { intent, error: 'Chưa thể hoàn tất thao tác. Vui lòng thử lại.' }
  }
}

