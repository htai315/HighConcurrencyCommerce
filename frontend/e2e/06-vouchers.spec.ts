import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

test.describe('E2E Flow 6: Basic Vouchers Lifecycle (T38)', () => {
  test('Complete Voucher Journey: Admin creates & activates voucher -> Customer applies at cart & checkouts COD -> Verification of limits & fulfillment consumption', async ({ browser }) => {
    const timestamp = Date.now();
    const voucherCode = `E2E${timestamp.toString().slice(-6)}`;
    const voucherName = `Ưu đãi E2E ${timestamp}`;

    // ----------------------------------------------------
    // PHASE 1: Admin creates & activates a voucher
    // ----------------------------------------------------
    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();

    // 1. Admin login
    await adminPage.goto('/login');
    await adminPage.locator('input[name="email"]').fill('admin@badminton.local');
    await adminPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await adminPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(adminPage).toHaveURL('/');

    // 2. Open Admin Dashboard and switch to Vouchers tab
    await adminPage.goto('/admin');
    await expect(adminPage.getByRole('heading', { level: 1 })).toBeVisible();

    const vouchersTab = adminPage.locator('#admin-tab-vouchers');
    await vouchersTab.click();
    await expect(adminPage.getByRole('heading', { name: 'Quản lý mã giảm giá', exact: true })).toBeVisible();

    // 3. Open create form and fill fields
    await adminPage.getByRole('button', { name: '+ Tạo mã mới' }).click();
    await adminPage.locator('#vch-code').fill(voucherCode);
    await adminPage.locator('#vch-name').fill(voucherName);

    // Discount value: 50,000 VND
    await adminPage.locator('#vch-discount-value').fill('50000');

    // Min order: 100,000 VND
    await adminPage.locator('#vch-min-order').fill('100000');

    // Total usage limit: 2, Per customer limit: 1
    await adminPage.locator('#vch-usage-limit-total').fill('2');
    await adminPage.locator('#vch-per-customer').fill('1');

    // Total budget: 500,000 VND
    await adminPage.locator('#vch-total-budget').fill('500000');

    await adminPage.getByRole('button', { name: 'Tạo bản nháp' }).click();
    await expect(adminPage.getByText(`Đã tạo mã giảm giá "${voucherCode}" (Bản nháp).`)).toBeVisible();

    // 4. Activate the draft voucher
    const voucherRow = adminPage.locator('tr', { hasText: voucherCode });
    await expect(voucherRow).toBeVisible();
    await expect(voucherRow.getByText('Bản nháp')).toBeVisible();

    await voucherRow.getByRole('button', { name: 'Kích hoạt' }).click();
    await expect(adminPage.getByRole('heading', { name: 'Kích hoạt mã giảm giá' })).toBeVisible();
    await adminPage.locator('.confirm-dialog button.button-primary, .confirm-dialog button:has-text("Xác nhận")').click();

    await expect(adminPage.getByText(`Đã thực hiện "Kích hoạt" thành công cho mã "${voucherCode}".`)).toBeVisible();
    await expect(voucherRow.getByText('Đang mở')).toBeVisible();

    // ----------------------------------------------------
    // PHASE 2: Customer 1 applies voucher and checkouts COD
    // ----------------------------------------------------
    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();

    const buyerEmail = `e2e-vch-${timestamp}@badminton.local`;
    const buyerPassword = 'VoucherPass123!@#';
    const buyerName = `E2E Voucher Buyer ${timestamp}`;

    // 1. Register & verify email
    await customerPage.goto('/register');
    await customerPage.locator('input[name="fullName"]').fill(buyerName);
    await customerPage.locator('input[name="email"]').fill(buyerEmail);
    await customerPage.locator('input[name="password"]').fill(buyerPassword);
    await customerPage.locator('input[name="confirmPassword"]').fill(buyerPassword);
    await customerPage.locator('input[type="checkbox"]').check();
    await customerPage.getByRole('button', { name: 'Tạo tài khoản' }).click();
    await expect(customerPage).toHaveURL(/.*verify-email.*registered=1/);

    const regEmail = await waitForEmail(buyerEmail, { subject: 'Xác minh địa chỉ email', timeoutMs: 15_000 });
    expect(regEmail, 'Verification email must be delivered').not.toBeNull();
    const verifyToken = extractFragmentTokenFromText(regEmail!.HTML || regEmail!.Text);
    expect(verifyToken, 'Verify token must be present').toBeTruthy();

    await customerPage.goto(`/verify-email#token=${verifyToken}`);
    await expect(customerPage.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 });
    await customerPage.locator('a[href="/login?verified=1"]').click();

    // Login
    await customerPage.locator('input[name="email"]').fill(buyerEmail);
    await customerPage.locator('input[name="password"]').fill(buyerPassword);
    await customerPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(customerPage).toHaveURL('/');

    // Add shipping address
    await customerPage.goto('/account');
    await customerPage.locator('details.address-create summary').click();
    await customerPage.locator('input[name="recipientName"]').fill(buyerName);
    await customerPage.locator('#addrPhone').fill('0901234567');
    await customerPage.locator('input[name="addressLine"]').fill('456 Đường Cầu Lông');
    await customerPage.locator('input[name="province"]').fill('Hồ Chí Minh');
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click();
    await expect(customerPage.getByText('456 Đường Cầu Lông')).toBeVisible();

    // Select product from storefront and add to cart
    await customerPage.goto('/');
    const productCard = customerPage.locator('a[href^="/products/"]').first();
    await expect(productCard).toBeVisible();
    await productCard.click();

    await expect(customerPage).toHaveURL(/\/products\/[0-9a-f-]+/i);
    const addToCartBtn = customerPage.getByRole('button', { name: 'Thêm vào giỏ' });
    await expect(addToCartBtn).toBeVisible();
    await addToCartBtn.click();

    const viewCartLink = customerPage.getByRole('link', { name: 'Xem giỏ' });
    await expect(viewCartLink).toBeVisible({ timeout: 5_000 });
    await viewCartLink.click();
    await expect(customerPage).toHaveURL('/cart');

    // 2. Apply voucher
    const voucherInput = customerPage.locator('#voucher-code-input');
    await expect(voucherInput).toBeVisible();
    await voucherInput.fill(voucherCode);
    await customerPage.getByRole('button', { name: 'Áp dụng' }).click();

    // Verify discount is applied in UI
    await expect(customerPage.getByText(`Mã giảm (${voucherCode})`)).toBeVisible();
    await expect(customerPage.locator('.cart-discount')).toContainText('-50.000');
    await expect(customerPage.getByText('Tổng thanh toán')).toBeVisible();

    // 3. Place order (COD)
    const checkoutBtn = customerPage.locator('.checkout-button');
    await expect(checkoutBtn).toBeEnabled();
    await checkoutBtn.click();

    const successCard = customerPage.locator('.checkout-success');
    await expect(successCard).toBeVisible({ timeout: 15_000 });
    const orderNumber = (await successCard.locator('h2').textContent())?.trim();
    expect(orderNumber, 'Order number should be generated').toBeTruthy();

    await expect(successCard.getByText(voucherCode)).toBeVisible();
    await expect(successCard).toContainText('-50.000');

    // ----------------------------------------------------
    // PHASE 3: Customer 1 cannot exceed perCustomerLimit
    // ----------------------------------------------------
    await customerPage.goto('/');
    const productCard2 = customerPage.locator('a[href^="/products/"]').first();
    await productCard2.click();
    await customerPage.getByRole('button', { name: 'Thêm vào giỏ' }).click();
    await customerPage.getByRole('link', { name: 'Xem giỏ' }).click();
    await expect(customerPage).toHaveURL('/cart');

    await customerPage.locator('#voucher-code-input').fill(voucherCode);
    await customerPage.getByRole('button', { name: 'Áp dụng' }).click();
    await expect(customerPage.locator('[role="alert"]')).toContainText('hết lượt');

    // ----------------------------------------------------
    // PHASE 4: Staff confirms COD & fulfills -> CONSUMED
    // ----------------------------------------------------
    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();

    await staffPage.goto('/login');
    await staffPage.locator('input[name="email"]').fill('staff@badminton.local');
    await staffPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await staffPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(staffPage).toHaveURL('/');

    await staffPage.goto('/staff');
    await staffPage.locator('#staff-tab-cod').click();

    const codCard = staffPage.locator('.cod-order-card', { hasText: orderNumber! });
    await expect(codCard).toBeVisible({ timeout: 10_000 });
    await codCard.getByRole('button', { name: 'Xác nhận đơn COD' }).click();

    // Fulfillment
    const fulfillmentRow = staffPage.locator('.fulfillment-row', { hasText: orderNumber! });
    await expect(fulfillmentRow).toBeVisible({ timeout: 10_000 });

    // Tiếp nhận đơn
    const acceptBtn = fulfillmentRow.getByRole('button', { name: 'Tiếp nhận đơn' });
    await expect(acceptBtn).toBeVisible();
    await acceptBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PENDING_PACKING', { timeout: 10_000 });

    // Đóng gói
    const packBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận đóng gói' });
    await expect(packBtn).toBeVisible();
    await packBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PACKED', { timeout: 10_000 });

    // Bàn giao vận chuyển
    const handoverBtn = fulfillmentRow.getByRole('button', { name: 'Bàn giao & xuất kho' });
    await expect(handoverBtn).toBeVisible();
    await handoverBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('SHIPPING', { timeout: 10_000 });

    // Xác nhận giao & thu COD
    const referenceInput = fulfillmentRow.locator('input[placeholder*="COD-20261030-001"]');
    await expect(referenceInput).toBeVisible();
    await referenceInput.fill(`REF-VCH-${timestamp}`);

    const deliverBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận giao & thu COD' });
    await expect(deliverBtn).toBeVisible();
    await deliverBtn.click();
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công');

    // ----------------------------------------------------
    // PHASE 5: Admin verifies voucher consumed counter
    // ----------------------------------------------------
    await adminPage.bringToFront();
    await adminPage.reload();
    await vouchersTab.click();

    const updatedVoucherRow = adminPage.locator('tr', { hasText: voucherCode });
    await expect(updatedVoucherRow).toBeVisible();
    // Consumed count must be 1
    await expect(updatedVoucherRow).toContainText('0 / 1 / 2');
  });
});
