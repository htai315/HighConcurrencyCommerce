import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server } from './setup'
import {
  MAX_RECENT_PRODUCTS,
  RECENT_PRODUCTS_CHANGED_EVENT,
  RECENT_PRODUCTS_STORAGE_KEY,
  RECENT_TTL_MS,
  clearRecentProducts,
  isGuid,
  parseRecentProducts,
  pruneRecentProducts,
  readRecentProducts,
  recordProductView,
  removeRecentProduct,
} from '../features/recent-products/recent-products-storage'
import { RecentProductsProvider } from '../features/recent-products/RecentProductsContext'
import { RecentlyViewedSection } from '../features/recent-products/RecentlyViewedSection'
import { useRecentProducts } from '../features/recent-products/useRecentProducts'
import { RelatedProductsSection } from '../features/catalog/RelatedProductsSection'
import {
  parseProductSummaryBatch,
  parseRelatedProducts,
} from '../features/catalog/catalog.api'
import type { ProductSummary } from '../features/catalog/catalog.api'

const P1 = '10000000-0000-4000-8000-000000000001'
const P2 = '10000000-0000-4000-8000-000000000002'
const P3 = '10000000-0000-4000-8000-000000000003'

function RecentIdsProbe() {
  const { recentIds } = useRecentProducts()
  return <output data-testid="recent-ids">{recentIds.join(',')}</output>
}

function mockSummary(id: string, title: string, minPrice = 100000): ProductSummary {
  return {
    id,
    title,
    category: { id: '20000000-0000-4000-8000-000000000001', name: 'Vợt cầu lông', slug: 'vot-cau-long' },
    brand: { id: '30000000-0000-4000-8000-000000000001', name: 'Yonex', slug: 'yonex', country: 'Japan', websiteUrl: null },
    minPrice,
    inStock: true,
    imageUrl: `https://example.com/images/${id}.jpg`,
    averageRating: 4.8,
    reviewCount: 15,
  }
}

describe('T53: Product Discovery — Storage & Parsers', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.restoreAllMocks()
  })

  afterEach(() => {
    cleanup()
    localStorage.clear()
  })

  test('isGuid validates standard GUID format and rejects empty or malformed strings', () => {
    expect(isGuid(P1)).toBe(true)
    expect(isGuid('53000000-0000-0000-0000-0000000000a1')).toBe(true)
    expect(isGuid('00000000-0000-0000-0000-000000000000')).toBe(false)
    expect(isGuid('not-a-guid')).toBe(false)
    expect(isGuid('')).toBe(false)
    expect(isGuid('10000000-0000-4000-8000-00000000000g')).toBe(false)
  })

  test('parseRecentProducts handles corrupted JSON, non-array, empty and invalid values', () => {
    expect(parseRecentProducts(null)).toEqual([])
    expect(parseRecentProducts('')).toEqual([])
    expect(parseRecentProducts('{ corrupt json ')).toEqual([])
    expect(parseRecentProducts('{"not": "an array"}')).toEqual([])
    expect(parseRecentProducts('12345')).toEqual([])
    expect(parseRecentProducts('true')).toEqual([])
  })

  test('parseRecentProducts enforces TTL pruning, deduplication, future timestamp clamping and cap 12', () => {
    const now = Date.now()
    const expiredTime = now - RECENT_TTL_MS - 1000
    const toleratedFutureTime = now + 2 * 60 * 1000
    const invalidFutureTime = now + 10 * 60 * 1000

    const items = [
      { productId: P1, viewedAt: now - 5000 },
      { productId: P2, viewedAt: expiredTime }, // should be pruned
      { productId: P1, viewedAt: now - 1000 }, // duplicate P1, newer timestamp -> keep this one
      { productId: P3, viewedAt: toleratedFutureTime }, // small clock skew -> clamped to now
      { productId: '10000000-0000-4000-8000-000000000004', viewedAt: invalidFutureTime }, // rejected
      { productId: 'invalid-id', viewedAt: now }, // rejected
      { productId: '00000000-0000-0000-0000-000000000000', viewedAt: now }, // rejected
    ]

    const parsed = parseRecentProducts(JSON.stringify(items), now)
    expect(parsed.length).toBe(2)
    // P3 was clamped to now, so it should be first
    expect(parsed[0]!.productId).toBe(P3)
    expect(parsed[0]!.viewedAt).toBe(now)
    // P1 had two occurrences, kept the newer one (now - 1000)
    expect(parsed[1]!.productId).toBe(P1)
    expect(parsed[1]!.viewedAt).toBe(now - 1000)

    // Test cap 12
    const manyItems = Array.from({ length: 20 }).map((_, i) => ({
      productId: `10000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
      viewedAt: now - i * 1000,
    }))
    const capped = parseRecentProducts(JSON.stringify(manyItems), now)
    expect(capped.length).toBe(MAX_RECENT_PRODUCTS)
    expect(capped.length).toBe(12)
    expect(capped[0]!.productId).toBe('10000000-0000-4000-8000-000000000001')
  })

  test('storage actions: recordView, remove, clear, and prune dispatch events', () => {
    let eventFired = 0
    const listener = () => { eventFired++ }
    window.addEventListener(RECENT_PRODUCTS_CHANGED_EVENT, listener)

    try {
      // 1. Record view P1
      const step1 = recordProductView(P1)
      expect(step1.length).toBe(1)
      expect(step1[0]!.productId).toBe(P1)
      expect(eventFired).toBe(1)

      // 2. Record view P2
      const step2 = recordProductView(P2)
      expect(step2.length).toBe(2)
      expect(step2[0]!.productId).toBe(P2) // newest first
      expect(eventFired).toBe(2)

      // 3. Record view P1 again (moves to front)
      const step3 = recordProductView(P1)
      expect(step3.length).toBe(2)
      expect(step3[0]!.productId).toBe(P1) // moved to front
      expect(step3[1]!.productId).toBe(P2)

      // 4. Read directly from localStorage
      const read = readRecentProducts()
      expect(read.length).toBe(2)
      expect(read[0]!.productId).toBe(P1)

      // 5. Prune with validIds = [P2]
      const step4 = pruneRecentProducts([P2])
      expect(step4.length).toBe(1)
      expect(step4[0]!.productId).toBe(P2)

      // 6. Remove P2
      const step5 = removeRecentProduct(P2)
      expect(step5.length).toBe(0)

      // 7. Clear
      recordProductView(P3)
      clearRecentProducts()
      expect(readRecentProducts()).toEqual([])
    } finally {
      window.removeEventListener(RECENT_PRODUCTS_CHANGED_EVENT, listener)
    }
  })

  test('storage survives localStorage errors gracefully without crashing', () => {
    const originalSetItem = localStorage.setItem
    try {
      localStorage.setItem = () => {
        throw new Error('QuotaExceededError')
      }
      // Should not throw
      expect(() => recordProductView(P1)).not.toThrow()
      expect(() => clearRecentProducts()).not.toThrow()
    } finally {
      localStorage.setItem = originalSetItem
    }
  })

  test('read removes corrupted storage and a cross-tab key removal clears provider memory', async () => {
    localStorage.setItem(RECENT_PRODUCTS_STORAGE_KEY, '{broken')
    expect(readRecentProducts()).toEqual([])
    expect(localStorage.getItem(RECENT_PRODUCTS_STORAGE_KEY)).toBeNull()

    recordProductView(P1)
    render(<RecentProductsProvider><RecentIdsProbe /></RecentProductsProvider>)
    expect(screen.getByTestId('recent-ids')).toHaveTextContent(P1)

    localStorage.removeItem(RECENT_PRODUCTS_STORAGE_KEY)
    window.dispatchEvent(new StorageEvent('storage', { key: RECENT_PRODUCTS_STORAGE_KEY }))
    await waitFor(() => expect(screen.getByTestId('recent-ids')).toHaveTextContent(''))
  })

  test('API parsers: parseRelatedProducts and parseProductSummaryBatch validate properly', () => {
    const relatedPayload = {
      sourceProductId: P1,
      items: [mockSummary(P2, 'Sản phẩm 2')],
    }
    const parsedRelated = parseRelatedProducts(relatedPayload)
    expect(parsedRelated.sourceProductId).toBe(P1)
    expect(parsedRelated.items.length).toBe(1)
    expect(parsedRelated.items[0]!.id).toBe(P2)

    const batchPayload = {
      items: [mockSummary(P1, 'Sản phẩm 1'), mockSummary(P2, 'Sản phẩm 2')],
      requestedCount: 3,
      omittedCount: 1,
    }
    const parsedBatch = parseProductSummaryBatch(batchPayload)
    expect(parsedBatch.items.length).toBe(2)
    expect(parsedBatch.requestedCount).toBe(3)
    expect(parsedBatch.omittedCount).toBe(1)

    expect(() => parseRelatedProducts({ ...relatedPayload, sourceProductId: 'not-a-guid' })).toThrow()
    expect(() => parseRelatedProducts({ ...relatedPayload, items: [mockSummary(P2, 'A'), mockSummary(P2, 'B')] })).toThrow()
    expect(() => parseProductSummaryBatch({ ...batchPayload, requestedCount: 4 })).toThrow()
    expect(() => parseProductSummaryBatch({
      items: [{ ...mockSummary(P1, 'Sản phẩm 1'), averageRating: 6 }],
      requestedCount: 1,
      omittedCount: 0,
    })).toThrow()

    const sanitized = parseProductSummaryBatch({
      items: [{ ...mockSummary(P1, 'Sản phẩm 1'), imageUrl: 'javascript:alert(1)' }],
      requestedCount: 1,
      omittedCount: 0,
    })
    expect(sanitized.items[0]!.imageUrl).toBeNull()
  })
})

describe('T53: RelatedProductsSection component', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    cleanup()
  })

  test('renders loading skeleton and then displays related products', async () => {
    server.use(
      http.get('*/api/v1/catalog/products/:id/related', () => {
        return HttpResponse.json({
          sourceProductId: P1,
          items: [mockSummary(P2, 'Vợt Yonex Astrox 88D Pro'), mockSummary(P3, 'Vợt Yonex Nanoflare 800')],
        })
      })
    )

    render(
      <MemoryRouter>
        <RelatedProductsSection productId={P1} />
      </MemoryRouter>
    )

    // Initially loading skeleton
    expect(screen.getByTestId('related-products-loading')).toBeInTheDocument()

    // Loaded
    await waitFor(() => {
      expect(screen.getByTestId('related-products-section')).toBeInTheDocument()
    })

    expect(screen.getByText('Sản phẩm tương tự')).toBeInTheDocument()
    expect(screen.getByText('Vợt Yonex Astrox 88D Pro')).toBeInTheDocument()
    expect(screen.getByText('Vợt Yonex Nanoflare 800')).toBeInTheDocument()
  })

  test('returns null when items is empty', async () => {
    server.use(
      http.get('*/api/v1/catalog/products/:id/related', () => {
        return HttpResponse.json({
          sourceProductId: P1,
          items: [],
        })
      })
    )

    const { container } = render(
      <MemoryRouter>
        <RelatedProductsSection productId={P1} />
      </MemoryRouter>
    )

    await waitFor(() => {
      expect(screen.queryByTestId('related-products-loading')).not.toBeInTheDocument()
    })

    expect(container).toBeEmptyDOMElement()
  })

  test('renders error state with retry button on failure', async () => {
    let callCount = 0
    server.use(
      http.get('*/api/v1/catalog/products/:id/related', () => {
        callCount++
        if (callCount === 1) {
          return new HttpResponse(null, { status: 500 })
        }
        return HttpResponse.json({
          sourceProductId: P1,
          items: [mockSummary(P2, 'Vợt Yonex phục hồi')],
        })
      })
    )

    const user = userEvent.setup()
    render(
      <MemoryRouter>
        <RelatedProductsSection productId={P1} />
      </MemoryRouter>
    )

    await waitFor(() => {
      expect(screen.getByTestId('related-products-error')).toBeInTheDocument()
    })

    const retryBtn = screen.getByRole('button', { name: /Thử lại/i })
    await user.click(retryBtn)

    await waitFor(() => {
      expect(screen.getByTestId('related-products-section')).toBeInTheDocument()
      expect(screen.getByText('Vợt Yonex phục hồi')).toBeInTheDocument()
    })
  })
})

describe('T53: RecentlyViewedSection component', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    cleanup()
  })

  test('returns null when storage has no items', () => {
    const { container } = render(
      <RecentProductsProvider>
        <MemoryRouter>
          <RecentlyViewedSection />
        </MemoryRouter>
      </RecentProductsProvider>
    )

    expect(container).toBeEmptyDOMElement()
  })

  test('hydrates recent items, filters excludeProductId, and supports clearing history', async () => {
    recordProductView(P1)
    recordProductView(P2)
    recordProductView(P3)

    server.use(
      http.get('*/api/v1/catalog/products/batch', ({ request }) => {
        const url = new URL(request.url)
        const ids = url.searchParams.getAll('id')
        const items = ids.map(id => mockSummary(id, `Sản phẩm ${id.slice(-1)}`))
        return HttpResponse.json({
          items,
          requestedCount: ids.length,
          omittedCount: 0,
        })
      })
    )

    const user = userEvent.setup()
    render(
      <RecentProductsProvider>
        <MemoryRouter>
          <RecentlyViewedSection excludeProductId={P3} />
        </MemoryRouter>
      </RecentProductsProvider>
    )

    // P3 was excluded, so only P2 and P1 should be fetched and rendered
    await waitFor(() => {
      expect(screen.getByTestId('recently-viewed-section')).toBeInTheDocument()
    })

    expect(screen.getByText('Bạn đã xem gần đây')).toBeInTheDocument()
    expect(screen.getByText('Sản phẩm 2')).toBeInTheDocument()
    expect(screen.getByText('Sản phẩm 1')).toBeInTheDocument()
    expect(screen.queryByText('Sản phẩm 3')).not.toBeInTheDocument()

    // Test clear action
    const clearBtn = screen.getByRole('button', { name: /Xóa/i })
    await user.click(clearBtn)

    // Section should disappear
    await waitFor(() => {
      expect(screen.queryByTestId('recently-viewed-section')).not.toBeInTheDocument()
    })

    expect(readRecentProducts()).toEqual([])
  })

  test('prunes omitted items from storage when server returns omittedCount > 0', async () => {
    recordProductView(P1)
    recordProductView(P2)

    server.use(
      http.get('*/api/v1/catalog/products/batch', () => {
        // P2 is unavailable/omitted on server
        return HttpResponse.json({
          items: [mockSummary(P1, 'Sản phẩm 1')],
          requestedCount: 2,
          omittedCount: 1,
        })
      })
    )

    render(
      <RecentProductsProvider>
        <MemoryRouter>
          <RecentlyViewedSection />
        </MemoryRouter>
      </RecentProductsProvider>
    )

    await waitFor(() => {
      expect(screen.getByTestId('recently-viewed-section')).toBeInTheDocument()
    })

    expect(screen.getByText('Sản phẩm 1')).toBeInTheDocument()
    // Local storage should now only contain P1, because P2 was omitted
    const stored = readRecentProducts()
    expect(stored.map(x => x.productId)).toEqual([P1])
  })

  test('pruning an omitted candidate preserves the current PDP product excluded from hydration', async () => {
    recordProductView(P1)
    recordProductView(P2)
    recordProductView(P3)

    server.use(
      http.get('*/api/v1/catalog/products/batch', () => HttpResponse.json({
        items: [mockSummary(P1, 'Sản phẩm 1')],
        requestedCount: 2,
        omittedCount: 1,
      }))
    )

    render(
      <RecentProductsProvider>
        <MemoryRouter>
          <RecentlyViewedSection excludeProductId={P3} />
        </MemoryRouter>
      </RecentProductsProvider>
    )

    await waitFor(() => expect(screen.getByText('Sản phẩm 1')).toBeInTheDocument())
    expect(readRecentProducts().map(item => item.productId)).toEqual([P3, P1])
  })
})

