import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import type { ReactNode } from 'react'
import { CompareButton } from '../features/comparison/CompareButton'
import { ComparisonProvider } from '../features/comparison/ComparisonContext'
import { ComparisonTray } from '../features/comparison/ComparisonTray'
import {
  COMPARISON_STORAGE_KEY,
  MAX_COMPARISON_ITEMS,
  buildComparisonUrl,
  isProductId,
  parseComparisonItems,
  resolveComparisonImageUrl,
} from '../features/comparison/comparison-storage'
import type { ComparisonItem } from '../features/comparison/comparison-storage'
import { parseProductComparison } from '../features/comparison/comparison.api'
import { useComparison } from '../features/comparison/useComparison'
import { appRoutes } from '../App'
import { server } from './setup'

const IDS = [
  '10000000-0000-4000-8000-000000000001',
  '10000000-0000-4000-8000-000000000002',
  '10000000-0000-4000-8000-000000000003',
  '10000000-0000-4000-8000-000000000004',
  '10000000-0000-4000-8000-000000000005',
]
const RACKET_CATEGORY = '20000000-0000-4000-8000-000000000001'
const SHOE_CATEGORY = '20000000-0000-4000-8000-000000000002'

function item(index: number, categoryId: string | null = RACKET_CATEGORY): ComparisonItem {
  return {
    id: IDS[index]!,
    title: `Sản phẩm ${index + 1}`,
    imageUrl: index === 0 ? 'images/product.webp' : null,
    categoryId,
    categoryName: categoryId === SHOE_CATEGORY ? 'Giày' : 'Vợt',
  }
}

function StateHarness() {
  const comparison = useComparison()
  return (
    <>
      <output aria-label="Số sản phẩm">{comparison.count}</output>
      <button type="button" onClick={() => comparison.add(item(0))}>Thêm một</button>
      <button type="button" onClick={() => comparison.add(item(1))}>Thêm hai</button>
      <button type="button" onClick={() => comparison.add(item(2))}>Thêm ba</button>
      <button type="button" onClick={() => comparison.add(item(3))}>Thêm bốn</button>
      <button type="button" onClick={() => comparison.add(item(4))}>Thêm năm</button>
      <button type="button" onClick={() => comparison.add(item(4, SHOE_CATEGORY))}>Thêm khác danh mục</button>
      <span role="status" aria-label="Thông báo so sánh">{comparison.notice}</span>
    </>
  )
}

function renderComparison(children: ReactNode) {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <ComparisonProvider>{children}</ComparisonProvider>
    </MemoryRouter>,
  )
}

describe('Product comparison state', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    localStorage.clear()
    vi.restoreAllMocks()
  })

  test('parser loại dữ liệu hỏng, deduplicate và cap bốn mục', () => {
    const parsed = parseComparisonItems([
      item(0),
      item(0),
      { ...item(1), title: '   Sản phẩm hợp lệ   ' },
      { ...item(2), id: 'not-a-uuid' },
      item(2), item(3), item(4),
    ])

    expect(parsed).toHaveLength(MAX_COMPARISON_ITEMS)
    expect(parsed.map(value => value.id)).toEqual(IDS.slice(0, 4))
    expect(parsed[1]?.title).toBe('Sản phẩm hợp lệ')
    expect(parseComparisonItems({ broken: true })).toEqual([])
    expect(isProductId('b0000000-0000-0000-0000-000000000001')).toBe(true)
    expect(isProductId('00000000-0000-0000-0000-000000000000')).toBe(false)
    expect(buildComparisonUrl(parsed)).toBe(`/compare?ids=${IDS.slice(0, 4).join('%2C')}`)
  })

  test('storage loại URL ảnh nguy hiểm và chuẩn hóa đường dẫn tương đối', () => {
    const parsed = parseComparisonItems([
      { ...item(0), imageUrl: 'javascript:alert(1)' },
      { ...item(1), imageUrl: '  images/product-2.webp  ' },
      { ...item(2), imageUrl: '//tracking.example/product.webp' },
      { ...item(3), imageUrl: 'https://cdn.example/product.webp' },
    ])

    expect(parsed.map(value => value.imageUrl)).toEqual([null, 'images/product-2.webp', null, 'https://cdn.example/product.webp'])
    expect(resolveComparisonImageUrl(parsed[1]?.imageUrl)).toBe('/images/product-2.webp')
  })

  test('CompareButton cập nhật aria, persistence và tray remove/clear', async () => {
    const user = userEvent.setup()
    renderComparison(
      <>
        <CompareButton product={item(0)} />
        <CompareButton product={item(1)} />
        <ComparisonTray />
      </>,
    )

    const first = screen.getByRole('button', { name: 'Thêm Sản phẩm 1 vào so sánh' })
    await user.click(first)
    expect(screen.getByTestId(`compare-btn-${IDS[0]}`)).toHaveAttribute('aria-pressed', 'true')
    expect(JSON.parse(localStorage.getItem(COMPARISON_STORAGE_KEY) ?? '[]')).toHaveLength(1)
    expect(screen.getByText('1/4 sản phẩm')).toBeVisible()

    await user.click(screen.getByRole('button', { name: 'Thêm Sản phẩm 2 vào so sánh' }))
    expect(screen.getByRole('link', { name: 'So sánh ngay (2)' })).toHaveAttribute('href', `/compare?ids=${IDS[0]}%2C${IDS[1]}`)
    const tray = screen.getByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })
    await user.click(within(tray).getByRole('button', { name: 'Bỏ Sản phẩm 1 khỏi so sánh' }))
    expect(screen.getByText('1/4 sản phẩm')).toBeVisible()
    await waitFor(() => expect(within(tray).getByRole('button', { name: 'Bỏ Sản phẩm 2 khỏi so sánh' })).toHaveFocus())
    await user.click(screen.getByRole('button', { name: 'Xóa tất cả' }))
    expect(screen.queryByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })).toBeNull()
  })

  test('chặn sản phẩm thứ năm và sản phẩm khác danh mục bằng live message', async () => {
    const user = userEvent.setup()
    renderComparison(<StateHarness />)

    await user.click(screen.getByRole('button', { name: 'Thêm một' }))
    await user.click(screen.getByRole('button', { name: 'Thêm khác danh mục' }))
    expect(screen.getByRole('status', { name: 'Thông báo so sánh' })).toHaveTextContent('cùng một danh mục')
    expect(screen.getByLabelText('Số sản phẩm')).toHaveTextContent('1')

    await user.click(screen.getByRole('button', { name: 'Thêm hai' }))
    await user.click(screen.getByRole('button', { name: 'Thêm ba' }))
    await user.click(screen.getByRole('button', { name: 'Thêm bốn' }))
    await user.click(screen.getByRole('button', { name: 'Thêm năm' }))
    expect(screen.getByRole('status', { name: 'Thông báo so sánh' })).toHaveTextContent('tối đa 4 sản phẩm')
    expect(screen.getByLabelText('Số sản phẩm')).toHaveTextContent('4')
  })

  test('giữ state trong memory khi localStorage bị chặn', async () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('blocked') })
    const user = userEvent.setup()
    renderComparison(<CompareButton product={item(0)} />)

    await user.click(screen.getByRole('button', { name: 'Thêm Sản phẩm 1 vào so sánh' }))
    expect(screen.getByTestId(`compare-btn-${IDS[0]}`)).toHaveAttribute('aria-pressed', 'true')
  })

  test('đồng bộ thay đổi localStorage từ tab khác', async () => {
    renderComparison(<StateHarness />)
    localStorage.setItem(COMPARISON_STORAGE_KEY, JSON.stringify([item(0), item(1)]))
    window.dispatchEvent(new StorageEvent('storage', { key: COMPARISON_STORAGE_KEY }))

    await waitFor(() => expect(screen.getByLabelText('Số sản phẩm')).toHaveTextContent('2'))
  })
})

const comparisonProducts = [
  {
    id: IDS[0], title: 'Astrox 88D Pro', description: 'Vợt tấn công',
    category: { id: RACKET_CATEGORY, name: 'Vợt', slug: 'vot' },
    brand: { id: '30000000-0000-4000-8000-000000000001', name: 'Yonex', slug: 'yonex', country: 'Nhật Bản', websiteUrl: null },
    skus: [{ id: '40000000-0000-4000-8000-000000000001', skuCode: 'AX88', optionLabel: '4U G5', price: 4_290_000, inStock: true }],
    images: [{ id: '50000000-0000-4000-8000-000000000001', fileKey: 'images/astrox.webp', altText: 'Astrox', displayOrder: 1 }],
    specifications: [
      { key: 'weight', displayName: 'Trọng lượng', value: '4U', displayOrder: 1 },
      { key: 'stiffness', displayName: 'Độ cứng', value: 'Cứng', displayOrder: 2 },
    ],
    averageRating: 4.8, reviewCount: 20,
  },
  {
    id: IDS[1], title: 'Thruster Ryuga II', description: 'Vợt công thủ',
    category: { id: RACKET_CATEGORY, name: 'Vợt', slug: 'vot' },
    brand: { id: '30000000-0000-4000-8000-000000000002', name: 'Victor', slug: 'victor', country: 'Đài Loan', websiteUrl: null },
    skus: [{ id: '40000000-0000-4000-8000-000000000002', skuCode: 'RYUGA', optionLabel: '4U G5', price: 3_850_000, inStock: false }],
    images: [],
    specifications: [
      { key: 'weight', displayName: 'Trọng lượng', value: '4U', displayOrder: 1 },
      { key: 'stiffness', displayName: 'Độ cứng thân', value: 'Trung bình cứng', displayOrder: 2 },
      { key: 'tension', displayName: 'Sức căng', value: '31 lbs', displayOrder: 3 },
    ],
    averageRating: 0, reviewCount: 0,
  },
]

describe('Compare page', () => {
  let router: ReturnType<typeof createMemoryRouter> | undefined
  let requests: URL[]

  beforeEach(() => {
    localStorage.clear()
    requests = []
    server.use(http.get('http://localhost:5173/api/v1/catalog/products/compare', ({ request }) => {
      const url = new URL(request.url)
      requests.push(url)
      const ids = url.searchParams.getAll('id')
      const items = ids.flatMap(id => comparisonProducts.filter(product => product.id === id))
      return HttpResponse.json({ items, requestedCount: ids.length, omittedCount: ids.length - items.length })
    }))
  })

  afterEach(() => {
    router?.dispose()
    localStorage.clear()
  })

  function open(path: string) {
    router = createMemoryRouter(appRoutes, { initialEntries: [path] })
    render(<RouterProvider router={router} />)
    return router
  }

  test('parser từ chối rating sai, thứ tự sai và response trộn danh mục', () => {
    const secondCategory = {
      ...comparisonProducts[1],
      category: { id: SHOE_CATEGORY, name: 'Giày', slug: 'giay' },
    }

    expect(() => parseProductComparison({
      items: [{ ...comparisonProducts[0], averageRating: 6 }], requestedCount: 1, omittedCount: 0,
    }, [IDS[0]!])).toThrow()
    expect(() => parseProductComparison({
      items: [comparisonProducts[1], comparisonProducts[0]], requestedCount: 2, omittedCount: 0,
    }, [IDS[0]!, IDS[1]!])).toThrow()
    expect(() => parseProductComparison({
      items: [comparisonProducts[0], secondCategory], requestedCount: 2, omittedCount: 0,
    }, [IDS[0]!, IDS[1]!])).toThrow()
  })

  test('loader gọi một batch request, giữ URL order và đồng bộ storage theo response public', async () => {
    localStorage.setItem(COMPARISON_STORAGE_KEY, JSON.stringify([item(4)]))
    open(`/compare?ids=${IDS[1]},${IDS[0]}`)

    expect(await screen.findByRole('heading', { name: 'Chọn đúng gear cho lối chơi của bạn' })).toBeVisible()
    expect(requests).toHaveLength(1)
    expect(requests[0]?.searchParams.getAll('id')).toEqual([IDS[1], IDS[0]])
    const columns = screen.getAllByRole('columnheader').slice(1)
    expect(within(columns[0]!).getByRole('heading', { name: 'Thruster Ryuga II' })).toBeVisible()
    expect(within(columns[1]!).getByRole('heading', { name: 'Astrox 88D Pro' })).toBeVisible()
    await waitFor(() => {
      const stored = JSON.parse(localStorage.getItem(COMPARISON_STORAGE_KEY) ?? '[]') as ComparisonItem[]
      expect(stored.map(value => value.id)).toEqual([IDS[1], IDS[0]])
    })
  })

  test('matrix union specs và chế độ chỉ hiện điểm khác nhau', async () => {
    const user = userEvent.setup()
    open(`/compare?ids=${IDS[0]},${IDS[1]}`)
    expect(await screen.findByRole('rowheader', { name: 'Trọng lượng' })).toBeVisible()
    expect(screen.getByRole('rowheader', { name: /Độ cứng/ })).toBeVisible()
    expect(screen.getByRole('rowheader', { name: /Sức căng/ })).toBeVisible()
    expect(screen.getByText('—')).toBeVisible()

    await user.click(screen.getByRole('checkbox', { name: 'Chỉ hiện điểm khác nhau' }))
    await waitFor(() => {
      expect(screen.queryByRole('rowheader', { name: 'Trọng lượng' })).toBeNull()
      expect(router?.state.location.search).toContain('different=true')
    })
    expect(screen.getByRole('rowheader', { name: /Độ cứng/ })).toBeVisible()
  })

  test('remove giữ bộ lọc khác biệt, chuyển focus và unavailable ID hiển thị notice generic', async () => {
    const user = userEvent.setup()
    open(`/compare?ids=${IDS[0]},${IDS[1]},${IDS[4]}&different=true`)
    expect(await screen.findByText('1 sản phẩm trong liên kết không còn khả dụng và đã được loại khỏi bảng.')).toBeVisible()

    await user.click(screen.getByRole('button', { name: 'Bỏ Astrox 88D Pro khỏi so sánh' }))
    await waitFor(() => {
      expect(router?.state.location.search).toBe(`?ids=${IDS[1]}&different=true`)
      expect(screen.getByRole('button', { name: 'Bỏ Thruster Ryuga II khỏi so sánh' })).toHaveFocus()
    })
    expect(await screen.findByText('Hãy thêm ít nhất một sản phẩm cùng danh mục để so sánh trực quan hơn.')).toBeVisible()
  })

  test('duplicate URL được canonicalize và route rỗng có empty state không gọi API', async () => {
    open(`/compare?ids=${IDS[0]},${IDS[0]}`)
    await waitFor(() => expect(router?.state.location.search).toBe(`?ids=${IDS[0]}`))
    expect(await screen.findByRole('heading', { name: 'Chọn đúng gear cho lối chơi của bạn' })).toBeVisible()
    expect(requests).toHaveLength(1)

    router?.dispose()
    router = undefined
    cleanup()
    requests = []
    open('/compare')
    expect(await screen.findByRole('heading', { name: 'Chưa có sản phẩm để so sánh' })).toBeVisible()
    expect(requests).toHaveLength(0)
  })

  test('minPrice an toàn không trả Infinity khi sản phẩm không có SKU', async () => {
    const productNoSkus = {
      ...comparisonProducts[0]!,
      skus: [],
    }
    server.use(
      http.get('http://localhost:5173/api/v1/catalog/products/batch', () => HttpResponse.json({
        items: [productNoSkus], requestedCount: 1, omittedCount: 0,
      }))
    )
    open(`/compare?ids=${IDS[0]}`)
    expect(await screen.findByRole('heading', { name: 'Chọn đúng gear cho lối chơi của bạn' })).toBeVisible()
    expect(screen.queryByText(/Infinity/i)).toBeNull()
  })
})
