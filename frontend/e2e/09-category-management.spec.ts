import { test, expect, type Page } from '@playwright/test'

async function login(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

test('E2E Flow 9: Admin category lifecycle updates Storefront taxonomy', async ({ browser }) => {
  const suffix = Date.now()
  const name = `Phụ kiện E2E ${suffix}`
  const slug = `phu-kien-e2e-${suffix}`
  const adminContext = await browser.newContext()
  const adminPage = await adminContext.newPage()
  await login(adminPage, 'admin@badminton.local', 'DemoPass123!@#')
  await adminPage.goto('/admin?section=categories')
  await expect(adminPage.getByRole('heading', { name: 'Quản trị Danh mục Sản phẩm' })).toBeVisible()
  await adminPage.getByLabel('Tên danh mục').fill(name)
  await adminPage.getByLabel('Slug URL').fill(slug)
  await adminPage.getByRole('button', { name: 'Tạo danh mục' }).click()
  await expect(adminPage.getByText('Đã tạo danh mục mới.')).toBeVisible()
  const row = adminPage.locator('.category-admin-row', { hasText: name })
  await expect(row).toBeVisible()

  const publicContext = await browser.newContext()
  const publicPage = await publicContext.newPage()
  await publicPage.goto('/')
  await expect(publicPage.getByRole('navigation', { name: 'Chọn danh mục sản phẩm' }).getByRole('link', { name })).toBeVisible()

  await row.getByRole('button', { name: 'Ẩn' }).click()
  await expect(row.getByRole('button', { name: 'Hiện' })).toBeVisible()
  await publicPage.reload()
  await expect(publicPage.getByRole('navigation', { name: 'Chọn danh mục sản phẩm' }).getByRole('link', { name })).toHaveCount(0)

  await row.getByRole('button', { name: 'Xóa' }).click()
  await adminPage.getByRole('button', { name: 'Xóa danh mục' }).click()
  await expect(adminPage.getByText(`Đã xóa danh mục “${name}”.`)).toBeVisible()
  await expect(adminPage.locator('.category-admin-row', { hasText: name })).toHaveCount(0)

  await publicContext.close()
  await adminContext.close()
})
