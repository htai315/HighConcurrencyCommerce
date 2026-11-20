import { expect, test, type Page } from '@playwright/test'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const productId = 'b0000000-0000-0000-0000-000000000001'
const fixture = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src/assets/badminton-hero-v2.webp')

async function login(page: Page, email: string) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

async function selectDemoProduct(page: Page, title: string) {
  await page.getByLabel('Tìm sản phẩm').fill('AX88D-PRO-4U')
  await page.getByRole('button', { name: new RegExp(title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click()
}

test('E2E Flow 13: structured revision review, suspended approval and durable rejected-media cleanup', async ({ browser, request }) => {
  test.setTimeout(90_000)
  const live = await (await request.get(`/api/v1/catalog/products/${productId}`)).json() as { title: string }
  const runId = Date.now()
  const proposedTitle = `${live.title.replace(/ · hardened \d+$/, '')} · hardened ${runId}`
  const proposedSpecName = `Kiểm chứng E2E ${runId}`

  const staffContext = await browser.newContext(); const staff = await staffContext.newPage()
  await login(staff, 'staff@badminton.local'); await staff.goto('/staff'); await selectDemoProduct(staff, live.title)
  await staff.getByRole('button', { name: 'Tạo bản chỉnh sửa' }).click()
  await staff.getByLabel('Tiêu đề đề xuất').fill(proposedTitle)
  await staff.getByLabel('Mô tả đề xuất').fill('Nội dung hardening được kiểm tra theo diff live và proposed.')
  await staff.getByRole('button', { name: 'Lưu revision' }).click(); await expect(staff.getByText('Đã lưu bản chỉnh sửa.')).toBeVisible()
  await staff.getByLabel('Khóa thông số revision').fill(`e2e_hardening_${runId}`)
  await staff.getByLabel('Tên thông số revision').fill(proposedSpecName)
  await staff.getByLabel('Giá trị thông số revision').fill('Đạt')
  await staff.getByRole('button', { name: 'Thêm thông số' }).click(); await expect(staff.getByText('Đã lưu thông số revision.')).toBeVisible()
  await staff.getByLabel('Ảnh revision mới').setInputFiles(fixture)
  await staff.getByLabel('Alt ảnh mới').fill('Ảnh revision hardening')
  await staff.getByRole('button', { name: 'Upload ảnh' }).click(); await expect(staff.getByText('Đã upload ảnh revision.')).toBeVisible()
  await staff.getByRole('button', { name: 'Gửi duyệt' }).click(); await expect(staff.getByText('Đã gửi Admin duyệt.')).toBeVisible()

  const adminContext = await browser.newContext(); const admin = await adminContext.newPage()
  await login(admin, 'admin@badminton.local'); await admin.goto('/admin'); await selectDemoProduct(admin, live.title)
  await admin.getByLabel('Lý do tạm ngừng').fill('Tạm ngừng để duyệt revision hardening')
  await admin.getByRole('button', { name: 'Tạm ngừng bán' }).click(); await admin.getByRole('button', { name: 'Xác nhận' }).click()
  await expect.poll(async () => (await request.get(`/api/v1/catalog/products/${productId}`)).status()).toBe(404)

  await admin.goto('/admin?section=revisions')
  await admin.getByRole('button', { name: new RegExp(live.title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click()
  await expect(admin.getByText(proposedTitle)).toBeVisible()
  await expect(admin.getByText(/Sản phẩm đang tạm ngừng bán/)).toBeVisible()
  await admin.getByRole('tab', { name: 'Hình ảnh' }).click(); await expect(admin.getByText('Thêm mới')).toBeVisible()
  await admin.getByRole('tab', { name: 'Thông số' }).click(); await expect(admin.getByText(proposedSpecName)).toBeVisible()
  await admin.getByRole('button', { name: 'Duyệt thay đổi' }).click(); await expect(admin.getByText('Đã duyệt và cập nhật Storefront.')).toBeVisible()
  expect((await request.get(`/api/v1/catalog/products/${productId}`)).status()).toBe(404)

  await admin.goto('/admin'); await selectDemoProduct(admin, proposedTitle)
  await admin.getByRole('button', { name: 'Mở bán lại' }).click(); await admin.getByRole('button', { name: 'Xác nhận' }).click()
  await expect.poll(async () => (await (await request.get(`/api/v1/catalog/products/${productId}`)).json() as { title: string }).title).toBe(proposedTitle)

  await staff.goto('/staff'); await selectDemoProduct(staff, proposedTitle); await staff.getByRole('button', { name: 'Tạo bản chỉnh sửa' }).click(); await expect(staff.getByText('Đã tạo bản chỉnh sửa.')).toBeVisible()
  const open = await staff.evaluate(async id => {
    const response = await fetch(`/api/v1/staff/products/${id}/revisions/open`, { credentials: 'same-origin', cache: 'no-store' })
    if (!response.ok) throw new Error(`Cannot load open revision: ${response.status}`)
    return response.json()
  }, productId) as { id: string }
  await staff.getByLabel('Ảnh revision mới').setInputFiles(fixture); await staff.getByLabel('Alt ảnh mới').fill('Ảnh sẽ cleanup')
  await staff.getByRole('button', { name: 'Upload ảnh' }).click(); await expect(staff.getByText('Đã upload ảnh revision.')).toBeVisible()
  await staff.getByRole('button', { name: 'Gửi duyệt' }).click(); await expect(staff.getByText('Đã gửi Admin duyệt.')).toBeVisible()
  await admin.goto('/admin?section=revisions'); await admin.getByRole('button', { name: new RegExp(proposedTitle.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click()
  await admin.getByPlaceholder('Nêu rõ nội dung Staff cần chỉnh sửa').fill('Ảnh chưa đạt tiêu chuẩn kiểm duyệt')
  await admin.getByRole('button', { name: 'Từ chối' }).click(); await expect(admin.getByText('Đã từ chối revision.')).toBeVisible()
  await expect.poll(async () => {
    const review = await admin.evaluate(async revisionId => {
      const response = await fetch(`/api/v1/admin/product-revisions/${revisionId}/review`, { credentials: 'same-origin', cache: 'no-store' })
      return response.ok ? response.json() : null
    }, open.id) as { images?: Array<{ cleanupStatus: string | null; previewAvailable: boolean }> } | null
    return review?.images?.some(image => image.cleanupStatus === 'COMPLETED' && !image.previewAvailable) ?? false
  }, { timeout: 20_000 }).toBe(true)

  await staffContext.close(); await adminContext.close()
})
