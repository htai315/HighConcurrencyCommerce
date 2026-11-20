import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'
import { setupServer } from 'msw/node'
import { http, HttpResponse } from 'msw'

// Route loaders and lazy modules can exceed Testing Library's 1-second default
// on a busy CI/Windows runner even though the suite-level timeout is 15 seconds.
// Keep async assertions bounded while avoiding load-dependent false negatives.
configure({ asyncUtilTimeout: 5_000 })

// jsdom does not implement the dialog top layer; real-browser tests cover focus and Escape.
if (!HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', '') }
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open') }
}

export const server = setupServer(
  http.get('http://localhost:5173/api/v1/cart', () => HttpResponse.json({ id: null, items: [], totalQuantity: 0, subtotal: 0, canCheckout: false, updatedAt: null })),
  http.get('http://localhost:5173/api/v1/auth/me', () => HttpResponse.json({ code: 'UNAUTHORIZED' }, { status: 401 })),
  http.get('http://localhost:5173/api/v1/auth/csrf', () => HttpResponse.json({ requestToken: 'csrf-test-token' })),
  http.get('http://localhost:5173/api/v1/orders', () => HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
  http.get('http://localhost:5173/api/v1/catalog/flash-sales', () => HttpResponse.json([])),
  http.get('http://localhost:5173/api/v1/catalog/search/suggestions', () => HttpResponse.json([])),
  http.get('http://localhost:5173/api/v1/auth/providers', () => HttpResponse.json({ google: { enabled: false, clientId: '' } })),
  http.get('http://localhost:5173/api/v1/payments/providers', () => HttpResponse.json({ payOs: { enabled: false } })),
  http.get('http://localhost:5173/api/v1/staff/categories', () => HttpResponse.json([])),
  http.get('http://localhost:5173/api/v1/staff/brands', () => HttpResponse.json([])),
  http.get('http://localhost:5173/api/v1/catalog/brands', () => HttpResponse.json([])),
  http.get('http://localhost:5173/api/v1/account/auth-methods', () => HttpResponse.json({ hasPassword: true, googleLinked: false })),
  http.get('http://localhost:5173/api/v1/catalog/products/:id/reviews', () => HttpResponse.json({
    items: [],
    summary: { averageRating: 0, reviewCount: 0, distribution: { one: 0, two: 0, three: 0, four: 0, five: 0 } },
    page: 1,
    pageSize: 10,
    totalCount: 0,
  })),
  http.get('http://localhost:5173/api/v1/products/:id/review-eligibility', () => HttpResponse.json({
    eligible: false,
    reasonCode: 'NO_DELIVERED_ORDER',
    existingReview: null,
  })),
  http.get('http://localhost:5173/api/v1/wishlist/summary', () => HttpResponse.json({ totalCount: 0, productIds: [] })),
  http.get('http://localhost:5173/api/v1/wishlist', () => HttpResponse.json({ items: [], page: 1, pageSize: 12, totalCount: 0 })),
  http.get('http://localhost:5173/api/v1/catalog/products/:id/related', ({ params }) => HttpResponse.json({
    sourceProductId: params.id,
    items: [],
  })),
  http.get('http://localhost:5173/api/v1/catalog/products/batch', () => HttpResponse.json({
    items: [],
    requestedCount: 0,
    omittedCount: 0,
  })),
  http.get('http://localhost:5173/api/v1/staff/exchange-shipments', () => HttpResponse.json([])),
  http.post('http://localhost:5173/api/v1/shipping/quotes', async ({ request }) => {
    const body = await request.json() as { shippingAddressId?: string }
    return HttpResponse.json({
      shippingAddressId: body.shippingAddressId ?? 'address-test',
      destinationProvinceKey: 'HO CHI MINH',
      merchandiseSubtotal: 4_800_000,
      quotes: [{
        shippingMethodId: 'shipping-standard', code: 'STANDARD', name: 'Giao hàng tiêu chuẩn', description: 'Giao tận nơi an toàn',
        shippingFee: 30_000, baseFee: 20_000, provinceSurcharge: 10_000, freeShippingApplied: false,
        freeShippingThreshold: null, estimatedMinDays: 2, estimatedMaxDays: 4, methodRevision: 3, isDefault: true,
      }],
    })
  }),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => { cleanup(); server.resetHandlers() })
afterAll(() => server.close())
