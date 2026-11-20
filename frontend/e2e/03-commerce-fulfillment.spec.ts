import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

test.describe('E2E Flow 3: Commerce, Checkout COD, Staff Fulfillment & Customer Order Feed', () => {
  test('Complete purchase journey: Customer orders COD -> Staff confirms and fulfills -> Customer verifies completed order and notifications', async ({ browser }) => {
    // ----------------------------------------------------
    // PHASE 1: Customer places an order via COD
    // ----------------------------------------------------
    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();

    const timestamp = Date.now();
    const buyerEmail = `e2e-buyer-${timestamp}@badminton.local`;
    const buyerPassword = 'BuyerPass123!@#';
    const buyerName = 'E2E Verified Buyer';

    // 1. Register & verify a dedicated customer for full test isolation (0 pending COD orders)
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
    const loginLink = customerPage.locator('a[href="/login?verified=1"]');
    await expect(loginLink).toBeVisible();
    await loginLink.click();
    await expect(customerPage).toHaveURL(/.*login.*/);

    // 2. Customer login
    await customerPage.locator('input[name="email"]').fill(buyerEmail);
    await customerPage.locator('input[name="password"]').fill(buyerPassword);
    await customerPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(customerPage).toHaveURL('/');

    // 3. Add shipping address
    await customerPage.goto('/account');
    await expect(customerPage.getByRole('heading', { name: 'Hồ sơ & Sổ địa chỉ' })).toBeVisible();
    await customerPage.locator('details.address-create summary').click();
    await customerPage.locator('input[name="recipientName"]').fill(buyerName);
    await customerPage.locator('#addrPhone').fill('0901234567');
    await customerPage.locator('input[name="addressLine"]').fill('123 Đường Cầu Lông');
    await customerPage.locator('input[name="province"]').fill('Hồ Chí Minh');
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click();
    await expect(customerPage.getByText('123 Đường Cầu Lông')).toBeVisible();

    // 4. Select product from Storefront
    await customerPage.goto('/');
    const productCard = customerPage.locator('a[href^="/products/"]').first();
    await expect(productCard).toBeVisible();
    await productCard.click();

    // 5. On Product Detail Page, choose SKU and add to cart
    await expect(customerPage).toHaveURL(/.*products\/.+/);
    const addToCartBtn = customerPage.getByRole('button', { name: 'Thêm vào giỏ' });
    await expect(addToCartBtn).toBeVisible();
    await addToCartBtn.click();
    await expect(customerPage.getByText('Đã thêm vào giỏ hàng.')).toBeVisible();

    // 6. Open Cart and proceed to checkout
    await customerPage.goto('/cart');
    await expect(customerPage.getByRole('heading', { name: 'Giỏ hàng' })).toBeVisible();

    // Place COD order
    const checkoutBtn = customerPage.locator('.checkout-button, button:has-text("Đặt hàng")');
    await expect(checkoutBtn).toBeVisible();
    await checkoutBtn.click();

    // Verify order created notice and extract exact order number
    const successCard = customerPage.locator('.checkout-success');
    await expect(successCard).toBeVisible({ timeout: 15_000 });
    const orderNumber = (await successCard.locator('h2').textContent())?.trim();
    expect(orderNumber, 'Order number should be generated').toBeTruthy();

    // ----------------------------------------------------
    // PHASE 2: Staff processes COD confirmation & Fulfillment
    // ----------------------------------------------------
    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();

    // 1. Staff login
    await staffPage.goto('/login');
    await staffPage.locator('input[name="email"]').fill('staff@badminton.local');
    await staffPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await staffPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(staffPage).toHaveURL('/');

    // 2. Open Staff Dashboard
    await staffPage.goto('/staff');
    await expect(staffPage.getByRole('heading', { level: 1 })).toBeVisible();

    // 3. Switch to COD & Fulfillment tab
    const codTab = staffPage.locator('#staff-tab-cod');
    await codTab.click();

    // 4. Confirm COD Order - target specifically our order card
    const codCard = staffPage.locator('.cod-order-card', { hasText: orderNumber! });
    await expect(codCard).toBeVisible({ timeout: 10_000 });
    const confirmCodBtn = codCard.getByRole('button', { name: 'Xác nhận đơn COD' });
    await expect(confirmCodBtn).toBeVisible();
    await confirmCodBtn.click();
    await expect(staffPage.locator('.cod-queue-header + p.alert, [role="alert"]')).toContainText(orderNumber!);

    // 5. Fulfillment processing - target specifically our order row
    const fulfillmentRow = staffPage.locator('.fulfillment-row', { hasText: orderNumber! });
    await expect(fulfillmentRow).toBeVisible({ timeout: 10_000 });

    // Step 5.1: Tiếp nhận đơn -> PENDING_PACKING
    const acceptFulfillmentBtn = fulfillmentRow.getByRole('button', { name: 'Tiếp nhận đơn' });
    await expect(acceptFulfillmentBtn).toBeVisible();
    await acceptFulfillmentBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PENDING_PACKING', { timeout: 10_000 });

    // Step 5.2: Xác nhận đóng gói -> PACKED
    const packBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận đóng gói' });
    await expect(packBtn).toBeVisible();
    await packBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('PACKED', { timeout: 10_000 });

    // Step 5.3: Bàn giao & xuất kho -> SHIPPING
    const handoverBtn = fulfillmentRow.getByRole('button', { name: 'Bàn giao & xuất kho' });
    await expect(handoverBtn).toBeVisible();
    await handoverBtn.click();
    await expect(fulfillmentRow.locator('[data-testid="shipment-status"]')).toHaveText('SHIPPING', { timeout: 10_000 });

    // Step 5.4: Confirm delivery & COD collected with unique reference
    const uniqueReceiptRef = `REF-E2E-${Date.now()}`;
    const referenceInput = fulfillmentRow.locator('input[placeholder*="COD-20261030-001"]');
    await expect(referenceInput).toBeVisible();
    await referenceInput.fill(uniqueReceiptRef);

    const deliverBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận giao & thu COD' });
    await expect(deliverBtn).toBeVisible();
    await deliverBtn.click();
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công');

    await staffContext.close();

    // ----------------------------------------------------
    // PHASE 3: Customer verifies order history and notifications
    // ----------------------------------------------------
    // 1. Customer checks /orders
    await customerPage.goto('/orders');
    await expect(customerPage.getByRole('heading', { name: 'Đơn hàng của tôi' })).toBeVisible();
    const customerOrderCard = customerPage.locator('.customer-order-card', { hasText: orderNumber! });
    await expect(customerOrderCard).toBeVisible();
    await expect(customerOrderCard).toContainText('Hoàn tất');

    // 2. Customer checks /notifications
    await customerPage.goto('/notifications');
    await expect(customerPage.getByRole('heading', { name: 'Thông báo của bạn' })).toBeVisible();

    const notificationCard = customerPage.locator('.notification-card', { hasText: orderNumber! }).first();
    await expect(notificationCard).toBeVisible();

    const markReadBtn = notificationCard.getByRole('button', { name: 'Đánh dấu đã đọc' });
    await expect(markReadBtn).toBeVisible();
    await markReadBtn.click();
    await expect(notificationCard.locator('.read-state')).toHaveText('Đã đọc');

    await customerContext.close();
  });
});

