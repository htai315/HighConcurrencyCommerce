import { expect, test, type Page } from '@playwright/test';
import {
  clearAllEmails,
  extractFragmentTokenFromText,
  listEmails,
  waitForEmail,
  waitForEmailCount,
} from './helpers/mailpit';

const demoPassword = 'DemoPass123!@#';

async function login(page: Page, email: string, password: string) {
  await page.goto('/login');
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.getByRole('button', { name: 'Đăng nhập' }).click();
  await expect(page).toHaveURL('/');
}

async function registerAndVerify(page: Page, email: string, password: string) {
  await page.goto('/register');
  await page.locator('input[name="fullName"]').fill('Khách hàng cảnh báo Wishlist');
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('input[type="checkbox"]').check();
  await page.getByRole('button', { name: 'Tạo tài khoản' }).click();
  await expect(page).toHaveURL(/verify-email.*registered=1/);

  const verificationEmail = await waitForEmail(email, {
    subject: 'Xác minh địa chỉ email',
    timeoutMs: 20_000,
  });
  expect(verificationEmail, 'Verification email must be delivered through Mailpit').not.toBeNull();
  const token = extractFragmentTokenFromText(verificationEmail!.HTML || verificationEmail!.Text);
  expect(token, 'Verification email must contain a fragment token').toBeTruthy();

  await page.goto(`/verify-email#token=${token}`);
  await expect(page.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 });
  await page.locator('a[href="/login?verified=1"]').click();
  await expect(page).toHaveURL(/login/);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.getByRole('button', { name: 'Đăng nhập' }).click();
  await expect(page).toHaveURL('/');
}

test.describe('E2E Flow 17: Wishlist product alerts', () => {
  test('Out-of-stock and price-drop alerts are delivered exactly once and stop after opt-out', async ({ browser }) => {
    test.setTimeout(150_000);
    await clearAllEmails();

    const suffix = Date.now();
    const productTitle = `Vợt cảnh báo E2E ${suffix}`;
    const skuCode = `ALERT-${suffix}`;
    const initialPrice = 2_000_000;
    const reducedPrice = 1_800_000;
    const postOptOutPrice = 1_700_000;

    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();
    await login(staffPage, 'staff@badminton.local', demoPassword);
    await staffPage.goto('/staff');
    await staffPage.getByLabel('Tên sản phẩm *').fill(productTitle);
    await staffPage.getByLabel('Mô tả tóm tắt').fill('Sản phẩm cô lập cho kiểm thử cảnh báo wishlist trên Docker.');
    await staffPage.getByRole('button', { name: 'Tạo bản nháp' }).click();
    await expect(staffPage.getByRole('heading', { name: productTitle, exact: true })).toBeVisible();

    const productMeta = staffPage.getByText(/^ID: [0-9a-f-]+/i);
    await expect(productMeta).toBeVisible();
    const productId = (await productMeta.textContent())?.match(/[0-9a-f]{8}-[0-9a-f-]{27,}/i)?.[0];
    expect(productId, 'Created product id must be rendered in the staff workspace').toBeTruthy();

    await staffPage.getByLabel('Mã SKU *').fill(skuCode);
    await staffPage.getByLabel('Tên phiên bản / Tùy chọn *').fill('4U G5');
    await staffPage.getByRole('button', { name: '+ Thêm SKU' }).click();
    await expect(staffPage.getByText(skuCode, { exact: true })).toBeVisible();

    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();
    await login(adminPage, 'admin@badminton.local', demoPassword);
    await adminPage.goto('/admin');
    const adminSearch = adminPage.getByLabel('Tìm sản phẩm');
    await adminSearch.fill(productTitle);
    await adminPage.getByRole('button', { name: new RegExp(productTitle) }).click();

    const skuRow = adminPage.getByRole('row').filter({ hasText: skuCode });
    const priceInput = skuRow.locator('input[type="number"]');
    await priceInput.fill(String(initialPrice));
    await skuRow.getByRole('button', { name: 'Lưu giá' }).click();
    await expect(skuRow).toContainText(/2[.,]000[.,]000/);
    await adminPage.getByRole('button', { name: 'Công bố sản phẩm' }).click();
    const confirmation = adminPage.getByRole('dialog');
    await expect(confirmation).toContainText('Công bố sản phẩm?');
    await confirmation.getByRole('button', { name: 'Xác nhận' }).click();
    await expect(adminPage.getByText('PUBLISHED', { exact: true }).last()).toBeVisible();

    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();
    const customerEmail = `e2e-alert-${suffix}@badminton.local`;
    const customerPassword = 'AlertPass123!@#';
    await registerAndVerify(customerPage, customerEmail, customerPassword);

    await customerPage.goto(`/products/${productId}`);
    const wishlistButton = customerPage.locator('.pdp-wishlist-btn');
    await expect(wishlistButton).toBeVisible();
    const wishlistSaved = customerPage.waitForResponse(response =>
      response.request().method() === 'PUT'
      && response.url().includes(`/api/v1/wishlist/items/${productId}`)
      && response.status() === 204);
    await wishlistButton.click();
    await wishlistSaved;
    await expect(wishlistButton).toHaveAttribute('aria-pressed', 'true');
    await expect(wishlistButton).toBeEnabled();
    await customerPage.goto('/wishlist');
    const wishlistCard = customerPage.getByTestId(`wishlist-card-${productId}`);
    await expect(wishlistCard).toContainText('Tạm hết hàng');

    await customerPage.getByTestId(`wishlist-alert-trigger-${productId}`).click();
    const alertDialog = customerPage.getByRole('dialog');
    await expect(alertDialog.getByLabel(/Có hàng trở lại/i)).toBeEnabled();
    await alertDialog.getByLabel(/Có hàng trở lại/i).check();
    await alertDialog.getByLabel(/Giá thường giảm/i).check();
    await alertDialog.getByLabel(/Gửi thêm qua email/i).check();
    await alertDialog.getByRole('button', { name: 'Lưu cảnh báo' }).click();
    await expect(alertDialog.getByRole('status')).toContainText('Đã lưu lựa chọn cảnh báo.');
    await alertDialog.getByRole('button', { name: 'Đóng', exact: true }).click();

    await staffPage.getByRole('button', { name: /Vận hành Kho & Tồn kho/ }).click();
    const stockInPanel = staffPage.locator('details').filter({ hasText: 'Nhập hàng vào kho' });
    await stockInPanel.locator('summary').click();
    await stockInPanel.locator('#stock-sku-select').selectOption({
      label: `${skuCode} — ${productTitle} (4U G5)`,
    });
    await stockInPanel.locator('#stock-qty-input').fill('12');
    await stockInPanel.locator('#stock-note-input').fill('Flow 17 back-in-stock trigger');
    await stockInPanel.getByRole('button', { name: /Xác nhận nhập kho/ }).click();
    await expect(staffPage.getByText(/Nhập kho thành công/)).toBeVisible();

    const stockEmail = await waitForEmail(customerEmail, {
      subject: 'Sản phẩm yêu thích đã có hàng',
      timeoutMs: 30_000,
    });
    expect(stockEmail, 'Back-in-stock email must be delivered').not.toBeNull();
    expect(`${stockEmail!.Text} ${stockEmail!.HTML}`).toContain(String(suffix));

    await customerPage.goto('/notifications');
    const stockNotifications = customerPage.locator('.notification-card').filter({ hasText: 'Có hàng trở lại' });
    await expect(stockNotifications).toHaveCount(1, { timeout: 30_000 });
    await customerPage.reload();
    await expect(customerPage.locator('.notification-card').filter({ hasText: 'Có hàng trở lại' })).toHaveCount(1);
    expect(await waitForEmailCount(customerEmail, 1, { subject: 'Sản phẩm yêu thích đã có hàng', timeoutMs: 5_000 })).toHaveLength(1);

    await adminPage.goto('/admin');
    await adminPage.getByLabel('Tìm sản phẩm').fill(productTitle);
    await adminPage.getByRole('button', { name: new RegExp(productTitle) }).click();
    const publishedSkuRow = adminPage.getByRole('row').filter({ hasText: skuCode });
    await publishedSkuRow.locator('input[type="number"]').fill(String(reducedPrice));
    await publishedSkuRow.getByRole('button', { name: 'Lưu giá' }).click();
    await expect(publishedSkuRow).toContainText(/1[.,]800[.,]000/);

    const priceEmail = await waitForEmail(customerEmail, {
      subject: 'Sản phẩm yêu thích vừa giảm giá',
      timeoutMs: 30_000,
    });
    expect(priceEmail, 'Price-drop email must be delivered').not.toBeNull();
    const normalizedPriceEmail = `${priceEmail!.Text} ${priceEmail!.HTML}`.replace(/[^0-9]/g, '');
    expect(normalizedPriceEmail).toContain(String(reducedPrice));

    await customerPage.reload();
    const priceNotifications = customerPage.locator('.notification-card').filter({ hasText: 'Giảm giá sản phẩm yêu thích' });
    await expect(priceNotifications).toHaveCount(1, { timeout: 30_000 });
    await customerPage.reload();
    await expect(customerPage.locator('.notification-card').filter({ hasText: 'Giảm giá sản phẩm yêu thích' })).toHaveCount(1);
    expect(await waitForEmailCount(customerEmail, 1, { subject: 'Sản phẩm yêu thích vừa giảm giá', timeoutMs: 5_000 })).toHaveLength(1);

    await customerPage.goto('/wishlist');
    await customerPage.getByTestId(`wishlist-alert-trigger-${productId}`).click();
    const optOutDialog = customerPage.getByRole('dialog');
    await optOutDialog.getByLabel(/Có hàng trở lại/i).uncheck();
    await optOutDialog.getByLabel(/Giá thường giảm/i).uncheck();
    await expect(optOutDialog.getByLabel(/Gửi thêm qua email/i)).not.toBeChecked();
    await optOutDialog.getByRole('button', { name: 'Lưu cảnh báo' }).click();
    await expect(optOutDialog.getByRole('status')).toContainText('Đã lưu lựa chọn cảnh báo.');
    await optOutDialog.getByRole('button', { name: 'Đóng', exact: true }).click();

    await adminPage.goto('/admin');
    await adminPage.getByLabel('Tìm sản phẩm').fill(productTitle);
    await adminPage.getByRole('button', { name: new RegExp(productTitle) }).click();
    const optOutSkuRow = adminPage.getByRole('row').filter({ hasText: skuCode });
    await optOutSkuRow.locator('input[type="number"]').fill(String(postOptOutPrice));
    await optOutSkuRow.getByRole('button', { name: 'Lưu giá' }).click();
    await expect(optOutSkuRow).toContainText(/1[.,]700[.,]000/);

    await customerPage.waitForTimeout(6_000);
    await customerPage.goto('/notifications');
    await expect(customerPage.locator('.notification-card').filter({ hasText: 'Có hàng trở lại' })).toHaveCount(1);
    await expect(customerPage.locator('.notification-card').filter({ hasText: 'Giảm giá sản phẩm yêu thích' })).toHaveCount(1);
    expect(await listEmails(customerEmail, { subject: 'Sản phẩm yêu thích đã có hàng' })).toHaveLength(1);
    expect(await listEmails(customerEmail, { subject: 'Sản phẩm yêu thích vừa giảm giá' })).toHaveLength(1);

    await customerContext.close();
    await adminContext.close();
    await staffContext.close();
  });
});
