import { expect, test, type Page } from '@playwright/test'

const firstProductId = 'b0000000-0000-0000-0000-000000000001'
const secondProductId = 'b0000000-0000-0000-0000-000000000002'
const firstTitle = 'Vợt cầu lông Yonex Astrox 88D Pro'
const secondTitle = 'Vợt cầu lông Victor Thruster Ryuga II'
const shoeTitle = 'Giày cầu lông Yonex Power Cushion 65 Z3'

async function loginAdmin(page: Page) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill('admin@badminton.local')
  await page.locator('input[name="password"]').fill('DemoPass123!@#')
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

test('E2E Flow 19: related products ranking, recently viewed hydration, cross-category guard, clear history, and lifecycle omission', async ({ browser }) => {
  test.setTimeout(120_000)

  // 1. Guest mở vợt A rồi vợt B; recent history hiển thị B trước A sau reload.
  const guestContext = await browser.newContext()
  const guest = await guestContext.newPage()

  // Mở vợt A
  await guest.goto(`/products/${firstProductId}`)
  await expect(guest.getByRole('heading', { level: 1, name: firstTitle })).toBeVisible()

  // Mở vợt B
  await guest.goto(`/products/${secondProductId}`)
  await expect(guest.getByRole('heading', { level: 1, name: secondTitle })).toBeVisible()

  // Trên PDP vợt B:
  // - Khối gần đây loại vợt B hiện tại, chỉ hiện vợt A
  const p2Recent = guest.getByTestId('recently-viewed-section')
  await expect(p2Recent).toBeVisible()
  await expect(p2Recent.getByRole('heading', { level: 2, name: 'Bạn đã xem gần đây' })).toBeVisible()
  await expect(p2Recent.getByRole('heading', { name: firstTitle })).toBeVisible()
  await expect(p2Recent.getByRole('heading', { name: secondTitle })).toHaveCount(0)

  // 2. Quay lại Storefront:
  // Storefront hiển thị B trước A trong khối "Bạn đã xem gần đây"
  await guest.goto('/')
  const storefrontRecent = guest.getByTestId('recently-viewed-section')
  await expect(storefrontRecent).toBeVisible()
  await expect(storefrontRecent.getByRole('heading', { level: 2, name: 'Bạn đã xem gần đây' })).toBeVisible()

  // Xác nhận B trước A qua thứ tự xuất hiện các card
  const recentCards = storefrontRecent.locator('.product-card')
  await expect(recentCards.nth(0)).toContainText(secondTitle)
  await expect(recentCards.nth(1)).toContainText(firstTitle)

  // Reload và xác nhận vẫn giữ B trước A (persistence localStorage)
  await guest.reload()
  await expect(guest.getByTestId('recently-viewed-section').locator('.product-card').nth(0)).toContainText(secondTitle)
  await expect(guest.getByTestId('recently-viewed-section').locator('.product-card').nth(1)).toContainText(firstTitle)

  // 3. PDP vợt A hiển thị related vợt B nhưng không hiển thị giày khác danh mục
  await guest.goto(`/products/${firstProductId}`)
  const relatedSection = guest.getByTestId('related-products-section')
  await expect(relatedSection).toBeVisible()
  await expect(relatedSection.getByRole('heading', { level: 2, name: 'Sản phẩm tương tự' })).toBeVisible()
  await expect(relatedSection.getByRole('heading', { name: secondTitle })).toBeVisible()
  await expect(relatedSection.getByRole('heading', { name: shoeTitle })).toHaveCount(0)

  // 4. Related card mở đúng PDP; Wishlist/Compare control vẫn hoạt động
  await expect(relatedSection.getByTestId(`compare-btn-${secondProductId}`)).toBeVisible()
  await relatedSection.getByRole('link', { name: secondTitle, exact: true }).click()
  await expect(guest).toHaveURL(`/products/${secondProductId}`)
  await expect(guest.getByRole('heading', { level: 1, name: secondTitle })).toBeVisible()

  // 5. Context trình duyệt mới không có recent history, chứng minh không lưu server/cookie account
  const freshContext = await browser.newContext()
  const freshPage = await freshContext.newPage()
  await freshPage.goto('/')
  await expect(freshPage.getByTestId('recently-viewed-section')).toHaveCount(0)
  await freshContext.close()

  // 6. Xóa recent history làm section biến mất
  await guest.goto('/')
  await expect(guest.getByTestId('recently-viewed-section')).toBeVisible()
  await guest.getByRole('button', { name: 'Xóa toàn bộ lịch sử sản phẩm đã xem gần đây' }).click()
  await expect(guest.getByTestId('recently-viewed-section')).toHaveCount(0)

  // Reload lại xác nhận vẫn trống
  await guest.reload()
  await expect(guest.getByTestId('recently-viewed-section')).toHaveCount(0)

  // 7. Admin suspend candidate; reload related/recent omit generic, không lộ SUSPENDED; resume trong finally
  const adminContext = await browser.newContext()
  const admin = await adminContext.newPage()
  let suspended = false
  try {
    // Guest xem lại vợt B để thêm vào recent
    await guest.goto(`/products/${secondProductId}`)
    await expect(guest.getByRole('heading', { level: 1, name: secondTitle })).toBeVisible()

    // Admin suspend vợt B
    await loginAdmin(admin)
    await admin.goto('/admin')
    await admin.getByLabel('Tìm sản phẩm').fill(secondTitle)
    await admin.getByRole('button', { name: new RegExp(secondTitle) }).click()
    await admin.getByLabel('Lý do tạm ngừng').fill('T53 Flow 19 kiểm tra generic discovery omission')
    await admin.getByRole('button', { name: 'Tạm ngừng bán' }).click()
    await admin.getByRole('button', { name: 'Xác nhận' }).click()
    suspended = true
    await expect(admin.getByText(/Đã chuyển sản phẩm sang SUSPENDED/)).toBeVisible()

    // Guest mở lại PDP vợt A:
    // Related products của A không còn vợt B
    await guest.goto(`/products/${firstProductId}`)
    const guestRelated = guest.getByTestId('related-products-section')
    if (await guestRelated.count() > 0) {
      await expect(guestRelated.getByRole('heading', { name: secondTitle })).toHaveCount(0)
    }
    // Recent products của A (đã xem B trước đó) tự động lọc bỏ B khi server omit
    const guestRecent = guest.getByTestId('recently-viewed-section')
    if (await guestRecent.count() > 0) {
      await expect(guestRecent.getByRole('heading', { name: secondTitle })).toHaveCount(0)
    }
    await expect(guest.getByText('SUSPENDED', { exact: true })).toHaveCount(0)
  } finally {
    if (suspended) {
      await admin.getByRole('button', { name: 'Mở bán lại' }).click()
      await admin.getByRole('button', { name: 'Xác nhận' }).click()
      await expect(admin.getByText(/Đã chuyển sản phẩm sang PUBLISHED/)).toBeVisible()
    }
    await adminContext.close()
    await guestContext.close()
  }
})

