import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { delay, http, HttpResponse } from 'msw'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { CatalogSearchBox } from '../features/catalog/CatalogSearchBox'
import { buildCatalogUrl, readCatalogSearchHistory, rememberCatalogSearch } from '../features/catalog/catalog-search'
import { server } from './setup'

const suggestionsApi = 'http://localhost:5173/api/v1/catalog/search/suggestions'
let router: ReturnType<typeof createMemoryRouter> | undefined

function suggestion(label: string, productId = 'product-1') {
  return { kind: 'PRODUCT', label, secondaryText: 'Yonex · Vợt cầu lông', productId, queryValue: label }
}

function open(path = '/') {
  router = createMemoryRouter([{
    path: '*',
    element: <CatalogSearchBox ariaLabel="Tìm sản phẩm thử nghiệm" />,
  }], { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

beforeEach(() => localStorage.clear())
afterEach(() => router?.dispose())

test('debounce không gọi API khi dưới 2 ký tự và chỉ gửi từ khóa cuối', async () => {
  const requests: string[] = []
  server.use(http.get(suggestionsApi, ({ request }) => {
    const query = new URL(request.url).searchParams.get('q') ?? ''
    requests.push(query)
    return HttpResponse.json([suggestion(`${query} Pro`)])
  }))
  const user = userEvent.setup()
  open()
  const input = screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' })

  await user.type(input, 'a')
  await delay(320)
  expect(requests).toEqual([])

  await user.type(input, 'st')
  await waitFor(() => expect(requests).toEqual(['ast']))
  expect(await screen.findByRole('option', { name: /ast Pro/i })).toBeVisible()
})

test('abort response cũ để gợi ý chậm không ghi đè từ khóa mới', async () => {
  server.use(http.get(suggestionsApi, async ({ request }) => {
    const query = new URL(request.url).searchParams.get('q') ?? ''
    await delay(query === 'alpha' ? 500 : 10)
    return HttpResponse.json([suggestion(query === 'alpha' ? 'Alpha cũ' : 'Beta mới')])
  }))
  const user = userEvent.setup()
  open()
  const input = screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' })

  await user.type(input, 'alpha')
  await delay(300)
  await user.clear(input)
  await user.type(input, 'beta')

  expect(await screen.findByRole('option', { name: /Beta mới/i })).toBeVisible()
  await delay(550)
  expect(screen.queryByText('Alpha cũ')).toBeNull()
})

test('hỗ trợ bàn phím, ARIA và mở đúng PDP khi chọn suggestion', async () => {
  server.use(http.get(suggestionsApi, () => HttpResponse.json([
    { kind: 'SKU', label: 'AX88D-PRO-4U', secondaryText: '4U / G5', productId: 'product-88', queryValue: 'AX88D-PRO-4U' },
  ])))
  const user = userEvent.setup()
  const route = open()
  const input = screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' })

  await user.type(input, 'AX88')
  await screen.findByRole('option', { name: /AX88D-PRO-4U/i })
  expect(input).toHaveAttribute('aria-expanded', 'true')
  await user.keyboard('{Escape}')
  expect(input).toHaveAttribute('aria-expanded', 'false')
  await user.keyboard('{ArrowDown}')
  expect(input).toHaveAttribute('aria-expanded', 'true')
  const activeOption = screen.getByRole('option', { name: /AX88D-PRO-4U/i })
  expect(activeOption).toHaveAttribute('aria-selected', 'true')
  expect(input).toHaveAttribute('aria-activedescendant', activeOption.id)
  await user.keyboard('{Enter}')

  await waitFor(() => expect(route.state.location.pathname).toBe('/products/product-88'))
  expect(readCatalogSearchHistory()).toEqual(['AX88D-PRO-4U'])
})

test('Enter không chọn suggestion tạo URL canonical, giữ filter và reset page', async () => {
  const user = userEvent.setup()
  const route = open('/?brandId=brand-1&page=4&sort=rating-desc')
  const input = screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' })
  await user.clear(input)
  await user.type(input, '  Astrox  ')
  await user.keyboard('{Enter}')

  await waitFor(() => expect(route.state.location.search).toBe('?q=Astrox&brandId=brand-1&sort=rating-desc'))
  expect(route.state.location.hash).toBe('#catalog')
})

test('lịch sử local tối đa 5, dedupe không dấu và cho phép xóa từng mục hoặc tất cả', async () => {
  for (const query of ['Một', 'Hai', 'Ba', 'Bốn', 'Năm', 'Sáu', 'Vợt Đỏ', 'vot do']) rememberCatalogSearch(query)
  expect(readCatalogSearchHistory()).toEqual(['vot do', 'Sáu', 'Năm', 'Bốn', 'Ba'])

  const user = userEvent.setup()
  open()
  const input = screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' })
  await user.click(input)
  expect(screen.getByRole('listbox', { name: 'Tìm kiếm gần đây' })).toBeVisible()
  expect(screen.getByText('Lịch sử chỉ lưu trên trình duyệt này.')).toBeVisible()

  await user.click(screen.getByRole('button', { name: 'Xóa vot do khỏi lịch sử' }))
  expect(screen.queryByText('vot do')).toBeNull()
  await user.click(screen.getByRole('button', { name: 'Xóa tất cả' }))
  expect(readCatalogSearchHistory()).toEqual([])
  expect(screen.queryByRole('listbox', { name: 'Tìm kiếm gần đây' })).toBeNull()
})

test('hiển thị lỗi gợi ý và thử lại thành công', async () => {
  let attempts = 0
  server.use(http.get(suggestionsApi, () => {
    attempts += 1
    return attempts === 1 ? HttpResponse.json({ code: 'TEMPORARY' }, { status: 503 }) : HttpResponse.json([suggestion('Astrox 88D Pro')])
  }))
  const user = userEvent.setup()
  open()
  await user.type(screen.getByRole('combobox', { name: 'Tìm sản phẩm thử nghiệm' }), 'astrox')

  expect(await screen.findByRole('alert')).toHaveTextContent('Chưa tải được gợi ý')
  await user.click(screen.getByRole('button', { name: 'Thử lại' }))
  expect(await screen.findByRole('option', { name: /Astrox 88D Pro/i })).toBeVisible()
  expect(attempts).toBe(2)
})

test('canonical helper bỏ default/rỗng và giữ thứ tự contract', () => {
  const source = new URLSearchParams('pageSize=24&sort=newest&maxPrice=5000000&q=astrox&page=1&categoryId=&inStock=true')
  expect(buildCatalogUrl(source)).toBe('/?q=astrox&maxPrice=5000000&inStock=true&sort=newest&pageSize=24#catalog')
})
