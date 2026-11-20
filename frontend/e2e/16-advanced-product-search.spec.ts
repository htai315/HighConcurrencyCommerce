import { expect, test, type Locator, type Page } from '@playwright/test'

const productId = 'b0000000-0000-0000-0000-000000000001'
const productTitle = 'Vợt cầu lông Yonex Astrox 88D Pro'
const exactSku = 'AX88D-PRO-4U'

async function loginAdmin(page: Page) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill('admin@badminton.local')
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

async function selectOptionContaining(select: Locator, text: string) {
  const value = await select.locator('option').filter({ hasText: text }).first().getAttribute('value')
  expect(value).toBeTruthy()
  await select.selectOption(value!)
}

test('E2E Flow 16: advanced search, facets, URL state, mobile drawer and lifecycle visibility', async ({ browser }) => {
  test.setTimeout(90_000)
  const shopperContext = await browser.newContext()
  const page = await shopperContext.newPage()

  // Search không dấu phải tìm đúng title tiếng Việt.
  await page.goto('/')
  const headerSearch = page.getByRole('combobox', { name: 'Tìm kiếm toàn cửa hàng' })
  await headerSearch.fill('vot cau long yonex astrox 88d pro')
  await headerSearch.press('Enter')
  await expect.poll(() => new URL(page.url()).searchParams.get('q'))
    .toBe('vot cau long yonex astrox 88d pro')
  await expect(page.locator('.product-card').getByRole('link', { name: productTitle }).first()).toBeVisible()

  // Exact SKU suggestion dùng bàn phím và mở thẳng PDP.
  await headerSearch.fill(exactSku)
  const skuOption = page.getByRole('option', { name: new RegExp(exactSku) }).first()
  await expect(skuOption).toBeVisible()
  await headerSearch.press('ArrowDown')
  await expect(skuOption).toHaveAttribute('aria-selected', 'true')
  await headerSearch.press('Enter')
  await expect(page).toHaveURL(`/products/${productId}`)
  await expect(page.getByRole('heading', { name: productTitle })).toBeVisible()

  // Facets kết hợp phải tạo URL canonical và vẫn trả đúng sản phẩm 5 sao.
  await page.goto('/?q=astrox&page=4#catalog')
  const filters = page.getByRole('search', { name: 'Bộ lọc danh mục sản phẩm' })
  await selectOptionContaining(filters.getByLabel('Danh mục'), 'Vợt cầu lông')
  await selectOptionContaining(filters.getByLabel('Thương hiệu'), 'Yonex')
  await filters.getByLabel('Giá từ (₫)').fill('4000000')
  await filters.getByLabel('Giá đến (₫)').fill('4500000')
  await filters.getByLabel(/Chỉ còn hàng/).check()
  const ratingSelect = filters.locator('select[name="minRating"]')
  const sortSelect = filters.locator('select[name="sort"]')
  await ratingSelect.selectOption('4')
  await sortSelect.selectOption('rating-desc')
  await filters.getByRole('button', { name: 'Áp dụng' }).click()

  await expect.poll(() => new URL(page.url()).searchParams.get('sort')).toBe('rating-desc')
  await expect(page.locator('.product-card').getByRole('link', { name: productTitle }).first()).toBeVisible()
  let url = new URL(page.url())
  expect(url.searchParams.get('q')).toBe('astrox')
  expect(url.searchParams.get('inStock')).toBe('true')
  expect(url.searchParams.get('minRating')).toBe('4')
  expect(url.searchParams.get('sort')).toBe('rating-desc')
  expect(url.searchParams.get('page')).toBeNull()
  expect(url.searchParams.get('brandId')).toBeTruthy()
  expect(url.searchParams.get('categoryId')).toBeTruthy()

  // Đổi sort, rồi back/forward phải phục hồi URL và controls.
  await sortSelect.selectOption('best-selling')
  await filters.getByRole('button', { name: 'Áp dụng' }).click()
  await expect.poll(() => new URL(page.url()).searchParams.get('sort')).toBe('best-selling')
  await page.goBack()
  await expect.poll(() => new URL(page.url()).searchParams.get('sort')).toBe('rating-desc')
  await expect(sortSelect).toHaveValue('rating-desc')
  await page.goForward()
  await expect.poll(() => new URL(page.url()).searchParams.get('sort')).toBe('best-selling')

  // Mobile drawer phải focus đúng, đóng bằng Escape và trả focus về trigger.
  await page.setViewportSize({ width: 390, height: 844 })
  const filterTrigger = page.getByRole('button', { name: /Bộ lọc/ })
  await filterTrigger.click()
  await expect(filterTrigger).toHaveAttribute('aria-expanded', 'true')
  const closeButton = page.locator('[data-filter-close]')
  await expect(closeButton).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(filterTrigger).toHaveAttribute('aria-expanded', 'false')
  await expect(filterTrigger).toBeFocused()

  // Suspend phải biến mất khỏi cả result và suggestion trong context mới.
  const adminContext = await browser.newContext()
  const admin = await adminContext.newPage()
  let suspended = false
  try {
    await loginAdmin(admin)
    await admin.goto('/admin')
    await admin.getByLabel('Tìm sản phẩm').fill(exactSku)
    await admin.getByRole('button', { name: new RegExp(productTitle) }).click()
    await admin.getByLabel('Lý do tạm ngừng').fill('T50 Flow 16 kiểm tra lifecycle visibility')
    await admin.getByRole('button', { name: 'Tạm ngừng bán' }).click()
    await admin.getByRole('button', { name: 'Xác nhận' }).click()
    suspended = true
    await expect(admin.getByText(/Đã chuyển sản phẩm sang SUSPENDED/)).toBeVisible()

    const freshContext = await browser.newContext()
    const fresh = await freshContext.newPage()
    await fresh.goto('/?q=astrox%2088d%20pro#catalog')
    await expect(fresh.getByText('Chưa tìm thấy sản phẩm phù hợp.')).toBeVisible()
    const freshSearch = fresh.getByRole('combobox', { name: 'Tìm kiếm toàn cửa hàng' })
    await freshSearch.fill(exactSku)
    await expect(fresh.getByRole('option', { name: new RegExp(exactSku) })).toHaveCount(0)
    await expect(fresh.getByText(/Không có gợi ý phù hợp/)).toBeVisible()
    await freshContext.close()
  } finally {
    if (suspended) {
      await admin.getByRole('button', { name: 'Mở bán lại' }).click()
      await admin.getByRole('button', { name: 'Xác nhận' }).click()
      await expect(admin.getByText(/Đã chuyển sản phẩm sang PUBLISHED/)).toBeVisible()
    }
    await adminContext.close()
    await shopperContext.close()
  }
})
