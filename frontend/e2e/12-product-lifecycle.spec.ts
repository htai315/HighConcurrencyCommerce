import { expect, test, type Page } from '@playwright/test'

const productId = 'b0000000-0000-0000-0000-000000000001'

async function login(page: Page, email: string) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

test('E2E Flow 12: Admin suspends and resumes while Staff can prepare a revision', async ({ browser, request }) => {
  const publicBefore = await request.get(`/api/v1/catalog/products/${productId}`)
  expect(publicBefore.status()).toBe(200)
  const title = ((await publicBefore.json()) as { title: string }).title
  const escapedTitle = title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

  const adminContext = await browser.newContext(); const admin = await adminContext.newPage()
  await login(admin, 'admin@badminton.local'); await admin.goto('/admin')
  await admin.getByLabel('Tìm sản phẩm').fill('AX88D-PRO-4U')
  await admin.getByRole('button', { name: new RegExp(escapedTitle) }).click()
  await admin.getByLabel('Lý do tạm ngừng').fill('Kiểm tra vòng đời sản phẩm bằng E2E')
  await admin.getByRole('button', { name: 'Tạm ngừng bán' }).click()
  await admin.getByRole('button', { name: 'Xác nhận' }).click()
  await expect(admin.getByText(/Đã chuyển sản phẩm sang SUSPENDED/)).toBeVisible()
  await expect.poll(async () => (await request.get(`/api/v1/catalog/products/${productId}`)).status()).toBe(404)

  const staffContext = await browser.newContext(); const staff = await staffContext.newPage()
  await login(staff, 'staff@badminton.local'); await staff.goto('/staff')
  await staff.getByLabel('Tìm sản phẩm').fill('AX88D-PRO-4U')
  await staff.getByRole('button', { name: new RegExp(escapedTitle) }).click()
  await expect(staff.getByText(/Sản phẩm đang tạm ngừng bán.*vẫn có thể soạn revision/)).toBeVisible()
  await expect(staff.getByRole('button', { name: 'Tạo bản chỉnh sửa' })).toBeVisible()

  await admin.getByRole('button', { name: 'Mở bán lại' }).click()
  await admin.getByRole('button', { name: 'Xác nhận' }).click()
  await expect(admin.getByText(/Đã chuyển sản phẩm sang PUBLISHED/)).toBeVisible()
  await expect.poll(async () => (await request.get(`/api/v1/catalog/products/${productId}`)).status()).toBe(200)

  await staffContext.close(); await adminContext.close()
})
