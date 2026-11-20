import { expect, test, type Page } from '@playwright/test'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const currentDirectory = path.dirname(fileURLToPath(import.meta.url))
const uploadFixture = path.resolve(currentDirectory, '../src/assets/badminton-hero-v2.webp')

async function login(page: Page) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill('staff@badminton.local')
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

test('E2E Flow 10: Staff uploads immutable product media and removes it safely', async ({ page, request }) => {
  const title = `Sản phẩm media E2E ${Date.now()}`
  const altText = `Ảnh cầu lông ${title}`

  await login(page)
  await page.goto('/staff')
  await expect(page.getByRole('heading', { name: 'Quản trị Sản phẩm & Media' })).toBeVisible()

  await page.getByLabel('Tên sản phẩm *').fill(title)
  await page.getByLabel('Mô tả tóm tắt').fill('Bản nháp dùng để kiểm chứng upload media trên Docker volume.')
  await page.getByRole('button', { name: 'Tạo bản nháp' }).click()
  await expect(page.getByText(`Tạo sản phẩm nháp "${title}" thành công.`)).toBeVisible()

  await page.locator('#new-img-files').setInputFiles(uploadFixture)
  await page.getByLabel('Alt text chung').fill(altText)
  await page.getByRole('button', { name: 'Upload 1 ảnh' }).click()
  await expect(page.getByText('Đã upload 1 ảnh thành công.')).toBeVisible()

  const uploadedImage = page.getByRole('img', { name: altText })
  await expect(uploadedImage).toBeVisible()
  const mediaUrl = await uploadedImage.getAttribute('src')
  expect(mediaUrl).toMatch(/^\/api\/v1\/media\/products\//)

  const publicResponse = await request.get(mediaUrl!)
  expect(publicResponse.status()).toBe(200)
  expect(publicResponse.headers()['content-type']).toBe('image/webp')
  expect(publicResponse.headers()['cache-control']).toContain('immutable')

  await page.getByRole('button', { name: 'Xóa ảnh' }).click()
  await page.getByRole('button', { name: 'Xác nhận xóa' }).click()
  await expect(page.getByText('Xóa ảnh thành công.')).toBeVisible()
  expect((await request.get(mediaUrl!)).status()).toBe(404)
})
