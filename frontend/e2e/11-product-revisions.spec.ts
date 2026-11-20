import { expect, test, type Page } from '@playwright/test'

const productId = 'b0000000-0000-0000-0000-000000000001'

async function login(page: Page, email: string) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

test('E2E Flow 11: Storefront changes only after Admin approves Staff revision', async ({ browser, request }) => {
  const before = await (await request.get(`/api/v1/catalog/products/${productId}`)).json() as { title: string }
  const proposedTitle = `${before.title.replace(/ · revision \d+$/, '')} · revision ${Date.now()}`

  const staffContext = await browser.newContext(); const staff = await staffContext.newPage()
  await login(staff, 'staff@badminton.local'); await staff.goto('/staff')
  await staff.getByLabel('Tìm sản phẩm').fill('AX88D-PRO-4U')
  const escapedLiveTitle = before.title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  await staff.getByRole('button', { name: new RegExp(escapedLiveTitle) }).click()
  await staff.getByRole('button', { name: 'Tạo bản chỉnh sửa' }).click()
  const titleInput = await staff.getByLabel('Tiêu đề đề xuất'); await titleInput.fill(proposedTitle)
  await staff.getByRole('button', { name: 'Lưu revision' }).click(); await expect(staff.getByText('Đã lưu bản chỉnh sửa.')).toBeVisible()
  expect((await (await request.get(`/api/v1/catalog/products/${productId}`)).json() as { title: string }).title).toBe(before.title)
  await staff.getByRole('button', { name: 'Gửi duyệt' }).click(); await expect(staff.getByText('Đã gửi Admin duyệt.')).toBeVisible()

  const adminContext = await browser.newContext(); const admin = await adminContext.newPage()
  await login(admin, 'admin@badminton.local'); await admin.goto('/admin?section=revisions')
  await expect(admin.getByRole('heading', { name: 'Duyệt thay đổi Sản phẩm' })).toBeVisible()
  await admin.getByRole('button', { name: new RegExp(escapedLiveTitle) }).click()
  await expect(admin.getByText(proposedTitle)).toBeVisible()
  await admin.getByRole('button', { name: 'Duyệt thay đổi' }).click(); await expect(admin.getByText('Đã duyệt và cập nhật Storefront.')).toBeVisible()
  await expect.poll(async () => (await (await request.get(`/api/v1/catalog/products/${productId}`)).json() as { title: string }).title).toBe(proposedTitle)

  await staffContext.close(); await adminContext.close()
})
