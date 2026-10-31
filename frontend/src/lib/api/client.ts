import { env } from '../../config/env'

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly correlationId?: string

  constructor(status: number, body?: unknown) {
    super(`API request failed (${status})`)
    this.name = 'ApiError'
    this.status = status
    if (isRecord(body)) {
      this.code = typeof body.code === 'string' ? body.code : undefined
      this.correlationId = typeof body.correlationId === 'string' ? body.correlationId : undefined
    }
  }
}

// Responses cross a trust boundary: each feature validates unknown JSON before use.
export async function apiRequest(path: string, init: RequestInit = {}): Promise<unknown> {
  const headers = new Headers(init.headers)
  if (!headers.has('Accept')) headers.set('Accept', 'application/json')
  if (typeof init.body === 'string' && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const response = await fetch(new URL(`${env.apiBaseUrl}${path}`, window.location.origin), { ...init, headers })
  if (!response.ok) throw new ApiError(response.status, await response.json().catch(() => undefined))
  if (response.status === 204) return undefined
  try { return await response.json() }
  catch { throw new ApiError(502, { code: 'INVALID_RESPONSE' }) }
}
