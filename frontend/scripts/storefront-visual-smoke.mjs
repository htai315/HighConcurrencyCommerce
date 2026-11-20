import { chromium, expect } from '@playwright/test'
import { mkdir } from 'node:fs/promises'

const baseUrl = process.env.VISUAL_SMOKE_URL ?? 'http://127.0.0.1:5173'
const output = new URL('../playwright-artifacts/', import.meta.url)
await mkdir(output, { recursive: true })
const browser = await chromium.launch({ channel: 'msedge', headless: true })

try {
  for (const [device, viewport] of [['desktop', { width: 1440, height: 1000 }], ['tablet', { width: 820, height: 1180 }], ['mobile', { width: 390, height: 844 }], ['small', { width: 320, height: 740 }]]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' })
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await page.goto(baseUrl, { waitUntil: 'networkidle' })
    await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible()
    await page.evaluate(() => document.fonts.ready)
    await page.screenshot({ path: new URL(`storefront-${device}.png`, output).pathname.replace(/^\/(\w:)/, '$1'), fullPage: true })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    expect(errors).toEqual([])
    // Browser-only fixtures exercise populated cards and filters without changing the database.
    const category = { id: 'rackets', name: 'Vợt cầu lông', slug: 'vot-cau-long' }
    const brand = { id: 'yonex', name: 'Yonex', slug: 'yonex', country: 'JP', websiteUrl: null }
    await page.route('**/api/v1/catalog/categories', route => route.fulfill({ json: [category, { id: 'shoes', name: 'Giày cầu lông', slug: 'giay' }] }))
    await page.route('**/api/v1/catalog/brands', route => route.fulfill({ json: [brand] }))
    await page.route('**/api/v1/catalog/search/suggestions?*', route => route.fulfill({ json: [] }))
    await page.route('**/api/v1/catalog/products?*', route => {
      const query = new URL(route.request().url()).searchParams
      const items = query.get('q') === 'khong-co' ? [] : Array.from({ length: 4 }, (_, index) => ({ id: `preview-${index}`, title: ['Vợt cầu lông cân bằng dành cho mọi lối chơi', 'Vợt cầu lông tốc độ', 'Vợt cầu lông kiểm soát', 'Vợt cầu lông tấn công'][index], category, minPrice: 1290000 + index * 300000, inStock: index !== 2, imageUrl: null }))
      return route.fulfill({ json: {
        items, totalCount: items.length, page: 1, pageSize: 12,
        query: { normalizedText: query.get('q'), effectiveSort: query.get('sort') ?? (query.has('q') ? 'relevance' : 'newest') },
        facets: { categories: [{ id: category.id, name: category.name, count: items.length }], brands: [{ id: brand.id, name: brand.name, count: items.length }], inStockCount: 3, priceMin: 1290000, priceMax: 2190000 },
      } })
    })
    await page.goto(baseUrl)
    await expect(page.locator('.product-card')).toHaveCount(4)
    await page.getByRole('combobox', { name: 'Tìm kiếm toàn cửa hàng', exact: true }).fill('khong-co')
    await page.getByRole('button', { name: 'Tìm kiếm', exact: true }).click()
    await expect(page).toHaveURL(/q=khong-co/)
    await expect(page.getByRole('heading', { name: 'Chưa tìm thấy sản phẩm phù hợp.' })).toBeVisible()
    await page.getByRole('link', { name: 'Xem tất cả sản phẩm', exact: true }).click()
    await expect(page.locator('.product-card')).toHaveCount(4)
    await page.getByRole('navigation', { name: 'Chọn danh mục sản phẩm' }).getByRole('link', { name: 'Vợt cầu lông' }).click()
    await expect(page).toHaveURL(/categoryId=rackets/)
    if (viewport.width <= 780) {
      await page.getByRole('button', { name: /Bộ lọc/ }).click()
      await expect(page.getByRole('button', { name: 'Đóng bộ lọc' }).last()).toBeFocused()
      expect(await page.evaluate(() => document.body.style.overflow)).toBe('hidden')
    }
    await expect(page.getByRole('combobox', { name: 'Danh mục', exact: true })).toHaveValue('rackets')
    await page.screenshot({ path: new URL(`storefront-products-${device}.png`, output).pathname.replace(/^\/(\w:)/, '$1'), fullPage: true })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.getByRole('combobox', { name: 'Tìm sản phẩm', exact: true }).fill('khong-co')
    await page.getByRole('button', { name: 'Áp dụng', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Chưa tìm thấy sản phẩm phù hợp.' })).toBeVisible()
    await page.getByRole('link', { name: 'Xem tất cả sản phẩm', exact: true }).click()
    await expect(page.locator('.product-card')).toHaveCount(4)
    expect(errors).toEqual([])
    console.log(`${device}: real page, populated fixtures, categories, filters, reset and overflow passed`)
    await context.close()
  }
} finally {
  await browser.close()
}
