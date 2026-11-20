import { test, expect, type Page } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

async function login(page: Page, email: string, password: string) {
  await page.goto('/login');
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.getByRole('button', { name: 'Đăng nhập' }).click();
  await expect(page).toHaveURL('/');
}

test.describe('E2E Flow 8: Direct Product Exchange (T42/T42.1)', () => {
  test('Customer exchanges a delivered COD item for another SKU and receives replacement', async ({ browser }) => {
    test.setTimeout(120_000);

    const timestamp = Date.now();
    const email = `e2e-exchange-${timestamp}@badminton.local`;
    const password = 'ExchangePass123!@#';
    const name = `Khách đổi SKU ${timestamp}`;

    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();
    await customerPage.goto('/register');
    await customerPage.locator('input[name="fullName"]').fill(name);
    await customerPage.locator('input[name="email"]').fill(email);
    await customerPage.locator('input[name="password"]').fill(password);
    await customerPage.locator('input[name="confirmPassword"]').fill(password);
    await customerPage.locator('input[type="checkbox"]').check();
    await customerPage.getByRole('button', { name: 'Tạo tài khoản' }).click();
    await expect(customerPage).toHaveURL(/verify-email.*registered=1/);
    const verification = await waitForEmail(email, { subject: 'Xác minh địa chỉ email', timeoutMs: 15_000 });
    expect(verification).not.toBeNull();
    const token = extractFragmentTokenFromText(verification!.HTML || verification!.Text);
    await customerPage.goto(`/verify-email#token=${token}`);
    await expect(customerPage.getByText('Xác minh thành công!')).toBeVisible();
    await customerPage.locator('a[href="/login?verified=1"]').click();
    await customerPage.locator('input[name="email"]').fill(email);
    await customerPage.locator('input[name="password"]').fill(password);
    await customerPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(customerPage).toHaveURL('/');

    await customerPage.goto('/account');
    await customerPage.locator('details.address-create summary').click();
    await customerPage.locator('input[name="recipientName"]').fill(name);
    await customerPage.locator('#addrPhone').fill('0911222333');
    await customerPage.locator('input[name="addressLine"]').fill('42 Sân Cầu Lông T42');
    await customerPage.locator('input[name="province"]').fill('Hà Nội');
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click();
    await expect(customerPage.getByText('42 Sân Cầu Lông T42')).toBeVisible();

    // Use the stable two-SKU demo product. Catalog ordering can change when other
    // E2E flows create newer products, and an arbitrary first product may have
    // no valid replacement SKU for the exchange scenario.
    await customerPage.goto('/products/b0000000-0000-0000-0000-000000000001');
    await expect(customerPage).toHaveURL(/\/products\/b0000000-0000-0000-0000-000000000001/i);
    await customerPage.getByRole('button', { name: 'Thêm vào giỏ' }).click();
    await customerPage.getByRole('link', { name: 'Xem giỏ' }).click();
    const checkoutButton = customerPage.locator('.checkout-button');
    await expect(checkoutButton).toBeEnabled();
    await checkoutButton.click();
    const success = customerPage.locator('.checkout-success');
    await expect(success).toBeVisible();
    const orderNumber = (await success.locator('h2').textContent())!.trim();

    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();
    await login(staffPage, 'staff@badminton.local', 'DemoPass123!@#');
    await staffPage.goto('/staff');
    await staffPage.locator('#staff-tab-cod').click();
    const codCard = staffPage.locator('.cod-order-card', { hasText: orderNumber });
    await expect(codCard).toBeVisible();
    await codCard.getByRole('button', { name: 'Xác nhận đơn COD' }).click();
    const fulfillment = staffPage.locator('.fulfillment-row', { hasText: orderNumber });
    await fulfillment.getByRole('button', { name: 'Tiếp nhận đơn' }).click();
    await fulfillment.getByRole('button', { name: 'Xác nhận đóng gói' }).click();
    await fulfillment.getByRole('button', { name: 'Bàn giao & xuất kho' }).click();
    await fulfillment.locator('input[placeholder*="COD-20261030-001"]').fill(`EX-COD-${timestamp}`);
    await fulfillment.getByRole('button', { name: 'Xác nhận giao & thu COD' }).click();
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công');

    await customerPage.goto('/orders');
    const orderCard = customerPage.locator('.customer-order-card', { hasText: orderNumber });
    await orderCard.getByRole('button', { name: /Yêu cầu đổi \/ trả hàng/ }).click();
    const modal = customerPage.getByRole('dialog', { name: 'Yêu cầu đổi/trả hàng' });
    await modal.locator('input[value="EXCHANGE"]').check();
    const target = modal.locator('select[aria-label^="Phân loại thay thế"]').first();
    await expect(target.locator('option')).toHaveCount(2);
    await target.selectOption({ index: 1 });
    await modal.locator('#return-reason').selectOption('WRONG_SIZE');
    await modal.getByRole('button', { name: 'Gửi yêu cầu đổi/trả' }).click();
    await expect(customerPage.getByText(/Yêu cầu đổi\/trả đã được gửi thành công/i)).toBeVisible();

    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();
    await login(adminPage, 'admin@badminton.local', 'DemoPass123!@#');
    await adminPage.goto('/admin');
    await adminPage.locator('#admin-tab-returns').click();
    const adminRow = adminPage.locator('tr', { hasText: orderNumber });
    await expect(adminRow.getByText('Đổi SKU', { exact: true })).toBeVisible();
    await adminRow.getByRole('button', { name: 'Duyệt' }).click();
    await expect(adminPage.getByText(/giữ tồn SKU thay thế trong 7 ngày/i)).toBeVisible();
    await adminPage.getByRole('button', { name: 'Xác nhận duyệt' }).click();
    await expect(adminPage.getByText(new RegExp(`Đã phê duyệt yêu cầu cho đơn ${orderNumber}`, 'i'))).toBeVisible();

    await staffPage.reload();
    await staffPage.locator('#staff-tab-returns').click();
    const returnCard = staffPage.locator('.staff-return-card', { hasText: orderNumber });
    await expect(returnCard).toBeVisible();
    await returnCard.getByRole('button', { name: 'Nhận vào khu cách ly' }).click();
    await staffPage.locator('#receive-evidence').fill('EXCHANGE-E2E-RECEIVED');
    await staffPage.getByRole('button', { name: 'Xác nhận nhận vào cách ly' }).click();
    await returnCard.getByRole('button', { name: 'Kiểm định chất lượng' }).click();
    await staffPage.getByLabel('Ghi chú kiểm định').fill('Hàng cũ đủ điều kiện đổi');
    await staffPage.getByRole('button', { name: 'Hoàn tất kiểm định' }).click();
    await expect(staffPage.getByText(/Kiện sản phẩm thay thế đã sẵn sàng/i)).toBeVisible();

    const exchangePanel = staffPage.getByRole('region', { name: 'Giao sản phẩm thay thế' });
    await exchangePanel.getByRole('button', { name: 'Làm mới' }).click();
    const exchangeCard = exchangePanel.locator('.management-card', { hasText: orderNumber });
    await expect(exchangeCard).toBeVisible();
    await exchangeCard.getByRole('button', { name: 'Đóng gói' }).click();
    await expect(exchangeCard.getByRole('button', { name: 'Bàn giao vận chuyển' })).toBeVisible();
    await exchangeCard.getByRole('button', { name: 'Bàn giao vận chuyển' }).click();
    await expect(exchangeCard.getByRole('button', { name: 'Giao thành công' })).toBeVisible();
    await exchangeCard.getByRole('button', { name: 'Giao thành công' }).click();
    await expect(exchangeCard).toBeHidden();

    await customerPage.goto('/account/returns');
    const history = customerPage.locator('.customer-order-card', { hasText: orderNumber });
    await expect(history.locator('.badge-exchanged')).toHaveText('Đổi hàng thành công');
    await expect(history.getByLabel('Tiến trình đổi sản phẩm')).toBeVisible();
    await expect(history.getByText('Không thu/hoàn chênh lệch')).toBeVisible();

    await adminContext.close();
    await staffContext.close();
    await customerContext.close();
  });
});
