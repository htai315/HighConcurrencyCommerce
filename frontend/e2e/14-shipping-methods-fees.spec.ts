import { expect, test, type Page } from '@playwright/test'
import { extractFragmentTokenFromText, waitForEmail } from './helpers/mailpit'

const demoPassword = 'DemoPass123!@#'

async function login(page: Page, email: string, password = demoPassword) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

async function createShippingMethod(page: Page, values: {
  code: string
  name: string
  baseFee: string
  minDays: string
  maxDays: string
  order: string
}) {
  await page.getByRole('button', { name: 'Tạo phương thức', exact: true }).first().click()
  const form = page.locator('.shipping-admin-form')
  await form.getByLabel('Mã phương thức').fill(values.code)
  await form.getByLabel('Tên hiển thị').fill(values.name)
  await form.getByLabel('Phí cơ bản (VNĐ)').fill(values.baseFee)
  await form.getByLabel('ETA tối thiểu (ngày)').fill(values.minDays)
  await form.getByLabel('ETA tối đa (ngày)').fill(values.maxDays)
  await form.getByLabel('Thứ tự hiển thị').fill(values.order)
  await form.getByRole('button', { name: 'Tạo phương thức', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('Đã tạo phương thức')
  await expect(page.locator('.shipping-method-admin-list button', { hasText: values.name })).toBeVisible()
}

async function selectShippingMethod(page: Page, name: string) {
  const method = page.locator('.shipping-method-admin-list button', { hasText: name })
  await expect(method).toBeVisible()
  await method.click()
  await expect(page.locator('.shipping-method-overview').getByRole('heading', { name })).toBeVisible()
}

async function confirmAdminAction(page: Page) {
  const dialog = page.locator('dialog.confirm-dialog')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: 'Xác nhận' }).click()
}

async function activateSelectedMethod(page: Page) {
  await page.locator('.shipping-method-overview').getByRole('button', { name: 'Kích hoạt' }).click()
  await confirmAdminAction(page)
  await expect(page.getByRole('status')).toContainText('Đã kích hoạt')
}

async function updateBaseFee(page: Page, fee: string) {
  const overview = page.locator('.shipping-method-overview')
  await overview.getByRole('button', { name: 'Chỉnh sửa' }).click()
  const form = page.locator('.shipping-admin-form')
  await form.getByLabel('Phí cơ bản (VNĐ)').fill(fee)
  await form.getByRole('button', { name: 'Lưu cấu hình' }).click()
  await expect(page.getByRole('status')).toContainText('Đã cập nhật')
}

async function addFirstProductToCart(page: Page) {
  await page.goto('/')
  const productCard = page.locator('a[href^="/products/"]').first()
  await expect(productCard).toBeVisible()
  await productCard.click()
  await expect(page).toHaveURL(/\/products\/[0-9a-f-]+/i)
  await page.getByRole('button', { name: 'Thêm vào giỏ' }).click()
  await expect(page.getByText('Đã thêm vào giỏ hàng.')).toBeVisible()
  await page.goto('/cart')
  await expect(page.getByRole('heading', { name: 'Giỏ hàng' })).toBeVisible()
}

async function chooseShipping(page: Page, methodName: string, expectedFee: string) {
  const card = page.locator('.shipping-method-card', { hasText: methodName })
  await expect(card).toBeVisible({ timeout: 15_000 })
  await expect(card.locator('.shipping-method-fee')).toContainText(expectedFee)
  await card.locator('input[type="radio"]').check()
  await expect(card).toHaveClass(/is-selected/)
}

test.describe('E2E Flow 14: Shipping Methods & Fees (T48)', () => {
  test('Admin configures rates -> customer checkout snapshots fees -> stale quote is rejected -> staff collects exact COD total', async ({ browser }) => {
    test.setTimeout(180_000)

    const stamp = Date.now().toString().slice(-8)
    const standardCode = `STD${stamp}`
    const expressCode = `EXP${stamp}`
    const standardName = `Tiêu chuẩn E2E ${stamp}`
    const expressName = `Hỏa tốc E2E ${stamp}`

    const adminContext = await browser.newContext()
    const adminPage = await adminContext.newPage()
    await login(adminPage, 'admin@badminton.local')
    await adminPage.goto('/admin')
    await adminPage.locator('#admin-tab-shipping').click()
    await expect(adminPage.getByRole('heading', { name: 'Vận chuyển & Phí', exact: true })).toBeVisible()

    await createShippingMethod(adminPage, {
      code: standardCode, name: standardName, baseFee: '30000', minDays: '2', maxDays: '4', order: '20',
    })
    await selectShippingMethod(adminPage, standardName)
    await activateSelectedMethod(adminPage)
    await adminPage.locator('.shipping-method-overview').getByRole('button', { name: 'Đặt mặc định' }).click()
    await confirmAdminAction(adminPage)
    await expect(adminPage.getByRole('status')).toContainText('mặc định')

    await createShippingMethod(adminPage, {
      code: expressCode, name: expressName, baseFee: '50000', minDays: '0', maxDays: '1', order: '5',
    })
    await selectShippingMethod(adminPage, expressName)
    await activateSelectedMethod(adminPage)

    const rateForm = adminPage.locator('.shipping-rate-form')
    await rateForm.getByLabel('Tỉnh/thành').fill('Hồ Chí Minh')
    await rateForm.getByLabel('Phụ phí (VNĐ)').fill('15000')
    await rateForm.getByRole('button', { name: 'Lưu phụ phí' }).click()
    await expect(adminPage.getByRole('status')).toContainText('Đã lưu phụ phí')
    await expect(adminPage.locator('.shipping-rate-workspace')).toContainText('HỒ CHÍ MINH')

    const customerContext = await browser.newContext()
    const customerPage = await customerContext.newPage()
    const customerEmail = `e2e-shipping-${Date.now()}@badminton.local`
    const customerPassword = 'ShippingPass123!@#'
    const customerName = `E2E Shipping ${stamp}`

    await customerPage.goto('/register')
    await customerPage.locator('input[name="fullName"]').fill(customerName)
    await customerPage.locator('input[name="email"]').fill(customerEmail)
    await customerPage.locator('input[name="password"]').fill(customerPassword)
    await customerPage.locator('input[name="confirmPassword"]').fill(customerPassword)
    await customerPage.locator('input[type="checkbox"]').check()
    await customerPage.getByRole('button', { name: 'Tạo tài khoản' }).click()
    await expect(customerPage).toHaveURL(/verify-email.*registered=1/)

    const verificationEmail = await waitForEmail(customerEmail, { subject: 'Xác minh địa chỉ email', timeoutMs: 20_000 })
    expect(verificationEmail).not.toBeNull()
    const verificationToken = extractFragmentTokenFromText(verificationEmail!.HTML || verificationEmail!.Text)
    expect(verificationToken).toBeTruthy()
    await customerPage.goto(`/verify-email#token=${verificationToken}`)
    await expect(customerPage.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 })
    await customerPage.locator('a[href="/login?verified=1"]').click()
    await customerPage.locator('input[name="email"]').fill(customerEmail)
    await customerPage.locator('input[name="password"]').fill(customerPassword)
    await customerPage.getByRole('button', { name: 'Đăng nhập' }).click()
    await expect(customerPage).toHaveURL('/')

    await customerPage.goto('/account')
    await customerPage.locator('details.address-create summary').click()
    await customerPage.locator('input[name="recipientName"]').fill(customerName)
    await customerPage.locator('#addrPhone').fill('0901234567')
    await customerPage.locator('input[name="addressLine"]').fill('48 Đường Vận Chuyển')
    await customerPage.locator('input[name="province"]').fill('Hồ Chí Minh')
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click()
    await expect(customerPage.getByText('48 Đường Vận Chuyển')).toBeVisible()

    // First order proves the selected method, surcharge and immutable order snapshot.
    await addFirstProductToCart(customerPage)
    await expect(customerPage.locator('.shipping-method-card', { hasText: standardName })).toBeVisible({ timeout: 15_000 })
    await chooseShipping(customerPage, expressName, '65.000')
    await customerPage.locator('.checkout-button').click()
    const firstSuccess = customerPage.locator('.checkout-success')
    await expect(firstSuccess).toBeVisible({ timeout: 15_000 })
    await expect(firstSuccess).toContainText(expressName)
    await expect(firstSuccess).toContainText('65.000')
    const firstOrderNumber = (await firstSuccess.locator('h2').textContent())!.trim()

    await adminPage.bringToFront()
    await selectShippingMethod(adminPage, expressName)
    await updateBaseFee(adminPage, '70000')

    await customerPage.bringToFront()
    await customerPage.goto('/orders')
    const firstOrder = customerPage.locator('.customer-order-card', { hasText: firstOrderNumber })
    await expect(firstOrder).toBeVisible()
    await firstOrder.getByRole('button', { name: 'Xem chi tiết đơn' }).click()
    await expect(firstOrder.locator('.order-detail-panel')).toContainText(expressName)
    await expect(firstOrder.locator('.order-detail-panel')).toContainText('65.000')

    // A second quote becomes stale while the cart is open. Checkout must not auto-retry.
    await addFirstProductToCart(customerPage)
    await chooseShipping(customerPage, expressName, '85.000')

    await adminPage.bringToFront()
    await selectShippingMethod(adminPage, expressName)
    await updateBaseFee(adminPage, '90000')

    await customerPage.bringToFront()
    await customerPage.locator('.checkout-button').click()
    await expect(customerPage.getByRole('alert')).toContainText('Phí vận chuyển vừa thay đổi')
    await expect(customerPage.locator('.checkout-success')).toHaveCount(0)
    await chooseShipping(customerPage, expressName, '105.000')
    await customerPage.locator('.checkout-button').click()

    const secondSuccess = customerPage.locator('.checkout-success')
    await expect(secondSuccess).toBeVisible({ timeout: 15_000 })
    await expect(secondSuccess).toContainText(expressName)
    await expect(secondSuccess).toContainText('105.000')
    const secondOrderNumber = (await secondSuccess.locator('h2').textContent())!.trim()
    const secondTotal = (await secondSuccess.locator('div', { hasText: 'Tổng thanh toán' }).last().locator('strong').textContent())!.trim()

    const staffContext = await browser.newContext()
    const staffPage = await staffContext.newPage()
    await login(staffPage, 'staff@badminton.local')
    await staffPage.goto('/staff')
    await staffPage.locator('#staff-tab-cod').click()
    const codCard = staffPage.locator('.cod-order-card', { hasText: secondOrderNumber })
    await expect(codCard).toBeVisible({ timeout: 15_000 })
    await expect(codCard).toContainText(secondTotal)
    await codCard.getByRole('button', { name: 'Xác nhận đơn COD' }).click()

    const fulfillmentRow = staffPage.locator('.fulfillment-row', { hasText: secondOrderNumber })
    await expect(fulfillmentRow).toBeVisible({ timeout: 15_000 })
    await expect(fulfillmentRow).toContainText(secondTotal)
    await fulfillmentRow.getByRole('button', { name: 'Tiếp nhận đơn' }).click()
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PENDING_PACKING')
    await fulfillmentRow.getByRole('button', { name: 'Xác nhận đóng gói' }).click()
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PACKED')
    await fulfillmentRow.getByRole('button', { name: 'Bàn giao & xuất kho' }).click()
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('SHIPPING')
    await fulfillmentRow.locator('input[placeholder*="COD-20261030-001"]').fill(`SHIP-${stamp}`)
    await fulfillmentRow.getByRole('button', { name: 'Xác nhận giao & thu COD' }).click()
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công')

    await Promise.all([adminContext.close(), customerContext.close(), staffContext.close()])
  })
})
