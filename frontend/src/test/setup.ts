import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'
import { setupServer } from 'msw/node'
import { http, HttpResponse } from 'msw'

export const server = setupServer(
  http.get('http://localhost:5173/api/v1/auth/me', () => HttpResponse.json({ code: 'UNAUTHORIZED' }, { status: 401 })),
  http.get('http://localhost:5173/api/v1/auth/csrf', () => HttpResponse.json({ requestToken: 'csrf-test-token' })),
  http.get('http://localhost:5173/api/v1/orders', () => HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
  http.get('http://localhost:5173/api/v1/catalog/flash-sales', () => HttpResponse.json([])),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => { cleanup(); server.resetHandlers() })
afterAll(() => server.close())
