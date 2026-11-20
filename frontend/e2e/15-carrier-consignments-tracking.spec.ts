import { expect, test, type Page } from '@playwright/test'
import { extractFragmentTokenFromText, waitForEmail } from './helpers/mailpit'

const demoPassword = 'DemoPass123!@#'
const labelPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')

async function login(page: Page, email: string, password = demoPassword) {
  await page.goto('/login')
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

async function registerCustomer(page: Page, email: string, password: string, name: string) {
  await page.goto('/register')
  await page.locator('input[name="fullName"]').fill(name)
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.locator('input[name="confirmPassword"]').fill(password)
  await page.locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: 'Tạo tài khoản' }).click()
  await expect(page).toHaveURL(/verify-email.*registered=1/)
  const mail = await waitForEmail(email, { subject: 'Xác minh địa chỉ email', timeoutMs: 20_000 })
  expect(mail).not.toBeNull()
  const token = extractFragmentTokenFromText(mail!.HTML || mail!.Text)
  expect(token).toBeTruthy()
  await page.goto(`/verify-email#token=${token}`)
  await expect(page.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 })
  await page.locator('a[href="/login?verified=1"]').click()
  await page.locator('input[name="email"]').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await expect(page).toHaveURL('/')
}

async function addAddress(page: Page, name: string) {
  await page.goto('/account')
  await page.locator('details.address-create summary').click()
  await page.locator('input[name="recipientName"]').fill(name)
  await page.locator('#addrPhone').fill('0901234567')
  await page.locator('input[name="addressLine"]').fill('49 Đường Tracking Nội Bộ')
  await page.locator('input[name="province"]').fill('Hồ Chí Minh')
  await page.getByRole('button', { name: 'Thêm địa chỉ' }).click()
  await expect(page.getByText('49 Đường Tracking Nội Bộ')).toBeVisible()
}

async function placeOrder(page: Page) {
  await page.goto('/')
  await page.locator('a[href^="/products/"]').first().click()
  await expect(page).toHaveURL(/\/products\/[0-9a-f-]+/i)
  const addToCart = page.getByRole('button', { name: 'Thêm vào giỏ' })
  if (!(await addToCart.isVisible())) {
    const variantChoices = page.locator('.color-chip-btn')
    for (let index = 0; index < await variantChoices.count() && !(await addToCart.isVisible()); index += 1) {
      await variantChoices.nth(index).click()
    }
  }
  await expect(addToCart).toBeEnabled()
  await addToCart.click()
  await page.getByRole('link', { name: 'Xem giỏ' }).click()
  const checkout = page.locator('.checkout-button')
  await expect(checkout).toBeEnabled({ timeout: 15_000 })
  await checkout.click()
  const success = page.locator('.checkout-success')
  await expect(success).toBeVisible({ timeout: 15_000 })
  return (await success.locator('h2').textContent())!.trim()
}

async function prepareShipment(page: Page, orderNumber: string) {
  await page.goto('/staff')
  await page.locator('#staff-tab-cod').click()
  const codCard = page.locator('.cod-order-card', { hasText: orderNumber })
  await expect(codCard).toBeVisible({ timeout: 15_000 })
  await codCard.getByRole('button', { name: 'Xác nhận đơn COD' }).click()
  const row = page.locator('.fulfillment-row', { hasText: orderNumber })
  await expect(row).toBeVisible({ timeout: 15_000 })
  await row.getByRole('button', { name: 'Tiếp nhận đơn' }).click()
  await expect(row.locator('[data-testid="shipment-status"]')).toHaveText('PENDING_PACKING')
  await row.getByRole('button', { name: 'Xác nhận đóng gói' }).click()
  await expect(row.locator('[data-testid="shipment-status"]')).toHaveText('PACKED')
  const shipmentId = await row.getAttribute('data-shipment-id')
  expect(shipmentId).toMatch(/^[0-9a-f-]{36}$/i)
  return shipmentId!
}

async function createAndHandoverConsignment(page: Page, shipmentId: string, carrierName: string, trackingNumber: string) {
  await page.locator('#staff-tab-carriers').click()
  await expect(page.locator('#staff-carrier-title')).toBeVisible()
  await page.getByLabel('Shipment ID *').fill(shipmentId)
  const carrierSelect = page.getByLabel('Hãng vận chuyển *')
  const carrierValue = await carrierSelect.locator('option', { hasText: carrierName }).getAttribute('value')
  expect(carrierValue).toBeTruthy()
  await carrierSelect.selectOption(carrierValue!)
  await page.getByLabel('Mã tracking *').fill(trackingNumber)
  await page.getByLabel('Mã dịch vụ').fill('E2E-EXPRESS')
  await page.getByRole('button', { name: 'Tạo vận đơn' }).click()
  await expect(page.getByRole('status')).toContainText('Hãy tải nhãn trước khi bàn giao')
  await page.locator('.carrier-file-button input[type="file"]').setInputFiles({ name: 'label.png', mimeType: 'image/png', buffer: labelPng })
  await expect(page.getByRole('status')).toContainText('Đã tải nhãn vận chuyển')
  await page.getByRole('button', { name: 'Bàn giao hãng' }).click()
  const dialog = page.locator('dialog.confirm-dialog')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: 'Xác nhận bàn giao' }).click()
  await expect(page.getByRole('status')).toContainText('Đã bàn giao vận đơn')
}

async function recordEvent(page: Page, type: string, label: string) {
  await page.getByLabel('Sự kiện').selectOption(type)
  await page.getByLabel('Vị trí').fill('Trạm trung chuyển E2E')
  await page.getByLabel('Mô tả').fill(label)
  await page.getByRole('button', { name: 'Ghi nhận sự kiện' }).click()
  await expect(page.getByRole('status')).toContainText('Đã ghi nhận mốc tracking mới')
  await expect(page.locator('.carrier-timeline')).toContainText(label)
}

async function deliver(page: Page, orderNumber: string, reference: string) {
  await page.locator('#staff-tab-cod').click()
  const row = page.locator('.fulfillment-row', { hasText: orderNumber })
  await expect(row).toBeVisible({ timeout: 15_000 })
  await expect(row.locator('[data-testid="shipment-status"]')).toHaveText('SHIPPING')
  await row.locator('input[placeholder*="COD-20261030-001"]').fill(reference)
  await row.getByRole('button', { name: 'Xác nhận giao & thu COD' }).click()
  await expect(page.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công')
}

test.describe('E2E Flow 15: Carrier Consignments & Internal Tracking (T49)', () => {
  test('Admin configures carrier -> Staff labels/tracks shipments -> Customer sees delivered and retry timelines', async ({ browser }) => {
    test.setTimeout(240_000)
    const stamp = Date.now().toString().slice(-9)
    const carrierCode = `E2E${stamp}`
    const carrierName = `Carrier E2E ${stamp}`

    const adminContext = await browser.newContext()
    const adminPage = await adminContext.newPage()
    await login(adminPage, 'admin@badminton.local')
    await adminPage.goto('/admin')
    await adminPage.locator('#admin-tab-carriers').click()
    await adminPage.getByRole('button', { name: '+ Thêm hãng' }).click()
    await adminPage.getByLabel('Mã hãng *').fill(carrierCode)
    await adminPage.getByLabel('Tên hãng *').fill(carrierName)
    await adminPage.getByLabel('Hotline').fill('19001049')
    await adminPage.getByLabel('Mẫu URL tracking').fill('https://tracking.example/{trackingNumber}')
    await adminPage.getByRole('button', { name: 'Tạo hãng' }).click()
    await expect(adminPage.getByRole('status')).toContainText('Đã tạo hãng vận chuyển')
    await adminPage.getByRole('button', { name: 'Kích hoạt' }).click()
    await adminPage.locator('dialog.confirm-dialog').getByRole('button', { name: 'Kích hoạt' }).click()
    await expect(adminPage.getByRole('status')).toContainText('Đã kích hoạt hãng vận chuyển')

    const customerContext = await browser.newContext()
    const customerPage = await customerContext.newPage()
    const email = `e2e-tracking-${stamp}@badminton.local`
    const password = 'TrackingPass123!@#'
    const customerName = `Khách Tracking ${stamp}`
    await registerCustomer(customerPage, email, password, customerName)
    await addAddress(customerPage, customerName)

    const staffContext = await browser.newContext()
    const staffPage = await staffContext.newPage()
    await login(staffPage, 'staff@badminton.local')

    // Primary branch: handover -> pickup -> transit -> out for delivery -> delivered.
    const firstOrder = await placeOrder(customerPage)
    const firstShipment = await prepareShipment(staffPage, firstOrder)
    const firstTracking = `TRACK-${stamp}-A`
    await createAndHandoverConsignment(staffPage, firstShipment, carrierName, firstTracking)
    await recordEvent(staffPage, 'PICKED_UP', 'Hãng đã nhận kiện hàng')
    await recordEvent(staffPage, 'IN_TRANSIT', 'Kiện hàng đang trung chuyển')
    await recordEvent(staffPage, 'OUT_FOR_DELIVERY', 'Bưu tá đang giao đến khách')

    await customerPage.goto('/orders')
    const firstCustomerCard = customerPage.locator('.customer-order-card', { hasText: firstOrder })
    await expect(firstCustomerCard).toContainText(firstTracking)
    await firstCustomerCard.getByRole('button', { name: 'Xem hành trình' }).click()
    await expect(firstCustomerCard.locator('.public-tracking-timeline')).toContainText('Bưu tá đang giao đến khách')
    const external = firstCustomerCard.getByRole('link', { name: /Tra cứu trên website hãng/ })
    await expect(external).toHaveAttribute('rel', 'noopener noreferrer')
    await expect(external).toHaveAttribute('href', `https://tracking.example/${firstTracking}`)

    await deliver(staffPage, firstOrder, `COD-${stamp}-A`)
    await customerPage.goto('/orders')
    const deliveredFirst = customerPage.locator('.customer-order-card', { hasText: firstOrder })
    await expect(deliveredFirst).toContainText('Hoàn tất')
    await expect(deliveredFirst).toContainText('Đã giao')
    await deliveredFirst.getByRole('button', { name: 'Xem hành trình' }).click()
    await expect(deliveredFirst.locator('.public-tracking-timeline')).toContainText('Đã giao hàng và hoàn tất thu COD')

    // Recovery branch: delivery failure -> retry -> delivered; stock-out must not repeat.
    const secondOrder = await placeOrder(customerPage)
    const secondShipment = await prepareShipment(staffPage, secondOrder)
    const secondTracking = `TRACK-${stamp}-B`
    await createAndHandoverConsignment(staffPage, secondShipment, carrierName, secondTracking)
    await staffPage.locator('#staff-tab-cod').click()
    const secondRow = staffPage.locator('.fulfillment-row', { hasText: secondOrder })
    await expect(secondRow).toBeVisible()
    await secondRow.locator('input[placeholder="VD: Không liên hệ được khách"]').fill('Khách hẹn giao lại ngày mai')
    await secondRow.getByRole('button', { name: 'Ghi nhận giao thất bại' }).click()
    await expect(secondRow.locator('[data-testid="shipment-status"]')).toHaveText('DELIVERY_FAILED')
    await secondRow.getByRole('button', { name: 'Giao lại' }).click()
    await expect(secondRow.locator('[data-testid="shipment-status"]')).toHaveText('SHIPPING')
    await deliver(staffPage, secondOrder, `COD-${stamp}-B`)

    await customerPage.goto('/orders')
    const deliveredSecond = customerPage.locator('.customer-order-card', { hasText: secondOrder })
    await expect(deliveredSecond).toContainText(secondTracking)
    await expect(deliveredSecond).toContainText('Hoàn tất')
    await deliveredSecond.getByRole('button', { name: 'Xem hành trình' }).click()
    await expect(deliveredSecond.locator('.public-tracking-timeline')).toContainText('Bắt đầu giao lại lần 1')
    await expect(deliveredSecond.locator('.public-tracking-timeline')).toContainText('Đã giao hàng và hoàn tất thu COD')

    await Promise.all([adminContext.close(), customerContext.close(), staffContext.close()])
  })
})
