import { expect, test, type Page } from '@playwright/test'

const firstProductId = 'b0000000-0000-0000-0000-000000000001'
const secondProductId = 'b0000000-0000-0000-0000-000000000002'
const shoeProductId = 'b0000000-0000-0000-0000-000000000003'
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

function comparisonIds(page: Page) {
  return new URL(page.url()).searchParams.get('ids')?.split(',') ?? []
}

test('E2E Flow 18: comparison URL, matrix, persistence, category guard and lifecycle omission', async ({ browser }) => {
  test.setTimeout(120_000)
  const shopperContext = await browser.newContext()
  const shopper = await shopperContext.newPage()

  await shopper.goto('/?q=Yonex%20Astrox#catalog')
  await shopper.getByTestId(`compare-btn-${firstProductId}`).click()
  await shopper.goto('/?q=Victor%20Thruster%20Ryuga%20II#catalog')
  await shopper.getByTestId(`compare-btn-${secondProductId}`).click()
  const tray = shopper.getByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })
  await expect(tray).toContainText('2/4 sản phẩm')

  await shopper.reload()
  await expect(tray).toContainText('2/4 sản phẩm')
  await tray.getByRole('link', { name: 'So sánh ngay (2)' }).click()
  await expect(shopper).toHaveURL(/\/compare\?ids=/)
  expect(comparisonIds(shopper)).toEqual([firstProductId, secondProductId])

  await expect(shopper.getByRole('heading', { name: firstTitle })).toBeVisible()
  await expect(shopper.getByRole('heading', { name: secondTitle })).toBeVisible()
  await expect(shopper.getByRole('rowheader', { name: 'Giá thường từ' })).toBeVisible()
  await expect(shopper.getByRole('rowheader', { name: 'Tình trạng' })).toBeVisible()
  await expect(shopper.getByRole('rowheader', { name: 'Phiên bản đang bán' })).toBeVisible()
  await expect(shopper.getByRole('rowheader', { name: /Sức căng tối đa/ })).toBeVisible()
  await expect(shopper.getByText('28 lbs', { exact: true })).toBeVisible()
  await expect(shopper.getByText('31 lbs', { exact: true })).toBeVisible()

  const shareUrl = shopper.url()
  const sharedContext = await browser.newContext()
  const shared = await sharedContext.newPage()
  await shared.goto(shareUrl)
  await expect(shared.getByRole('heading', { name: firstTitle })).toBeVisible()
  await expect(shared.getByRole('heading', { name: secondTitle })).toBeVisible()
  expect(comparisonIds(shared)).toEqual([firstProductId, secondProductId])
  await sharedContext.close()

  await shopper.getByRole('checkbox', { name: 'Chỉ hiện điểm khác nhau' }).click()
  await expect.poll(() => new URL(shopper.url()).searchParams.get('different')).toBe('true')
  await expect(shopper.getByRole('rowheader', { name: 'Phân hạng trọng lượng' })).toHaveCount(0)
  await shopper.getByRole('button', { name: `Bỏ ${secondTitle} khỏi so sánh` }).click()
  await expect.poll(() => comparisonIds(shopper)).toEqual([firstProductId])
  await expect.poll(() => new URL(shopper.url()).searchParams.get('different')).toBe('true')

  await shopper.goto(`/products/${secondProductId}`)
  await shopper.getByRole('button', { name: `Thêm ${secondTitle} vào so sánh` }).click()
  await expect(shopper.getByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })).toContainText('2/4 sản phẩm')
  await shopper.getByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })
    .getByRole('link', { name: 'So sánh ngay (2)' }).click()
  await expect.poll(() => comparisonIds(shopper)).toEqual([firstProductId, secondProductId])
  expect(new Set(comparisonIds(shopper)).size).toBe(2)

  await shopper.goto(`/products/${shoeProductId}`)
  await shopper.getByRole('button', { name: `Thêm ${shoeTitle} vào so sánh` }).click()
  await expect(shopper.getByRole('status')).toContainText('cùng một danh mục')
  await expect(shopper.getByRole('complementary', { name: 'Danh sách sản phẩm so sánh' })).toContainText('2/4 sản phẩm')

  const adminContext = await browser.newContext()
  const admin = await adminContext.newPage()
  let suspended = false
  try {
    await loginAdmin(admin)
    await admin.goto('/admin')
    await admin.getByLabel('Tìm sản phẩm').fill(secondTitle)
    await admin.getByRole('button', { name: new RegExp(secondTitle) }).click()
    await admin.getByLabel('Lý do tạm ngừng').fill('T52 Flow 18 kiểm tra generic unavailable state')
    await admin.getByRole('button', { name: 'Tạm ngừng bán' }).click()
    await admin.getByRole('button', { name: 'Xác nhận' }).click()
    suspended = true
    await expect(admin.getByText(/Đã chuyển sản phẩm sang SUSPENDED/)).toBeVisible()

    await shopper.goto(`/compare?ids=${firstProductId},${secondProductId}`)
    await expect(shopper.getByText('1 sản phẩm trong liên kết không còn khả dụng và đã được loại khỏi bảng.')).toBeVisible()
    await expect(shopper.getByRole('heading', { name: firstTitle })).toBeVisible()
    await expect(shopper.getByRole('heading', { name: secondTitle })).toHaveCount(0)
    await expect(shopper.getByText('SUSPENDED', { exact: true })).toHaveCount(0)
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
