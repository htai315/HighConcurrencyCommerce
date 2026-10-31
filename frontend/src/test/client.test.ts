import { delay, http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { ApiError, apiRequest } from '../lib/api/client'
import { server } from './setup'

const endpoint = 'http://localhost:5173/api/v1/probe'

test('giữ Headers của caller và Accept mặc định', async () => {
  server.use(http.get(endpoint, ({ request }) => HttpResponse.json({
    custom: request.headers.get('X-Probe'), accept: request.headers.get('Accept'),
  })))
  expect(await apiRequest('/probe', { headers: new Headers({ 'X-Probe': 'value' }) }))
    .toEqual({ custom: 'value', accept: 'application/json' })
})

test('HTTP lỗi với body HTML vẫn trả ApiError không lộ nội dung server', async () => {
  server.use(http.get(endpoint, () => new HttpResponse('<html>private proxy config</html>', { status: 502 })))
  await expect(apiRequest('/probe')).rejects.toEqual(new ApiError(502))
})

test('request bị hủy kết thúc bằng AbortError', async () => {
  server.use(http.get(endpoint, async () => { await delay(100); return HttpResponse.json({ ok: true }) }))
  const controller = new AbortController()
  const pending = apiRequest('/probe', { signal: controller.signal })
  controller.abort()
  await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
})

test('204 không cố đọc JSON rỗng', async () => {
  server.use(http.get(endpoint, () => new HttpResponse(null, { status: 204 })))
  expect(await apiRequest('/probe')).toBeUndefined()
})
