import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

test.describe('E2E Flow 7: COD Returns, Quality Inspection & Manual Refunds (T39/T41)', () => {
  test('Complete Return Journey: delivery -> return -> quarantine -> inspection -> manual refund -> reporting', async ({ browser }) => {
    const timestamp = Date.now();
    const buyerEmail = `e2e-ret-${timestamp}@badminton.local`;
    const buyerPassword = 'ReturnPass123!@#';
    const buyerName = `Khách hàng Đổi Trả ${timestamp}`;
    const refundEvidenceRef = `REFUND-UNC-${timestamp}`;

    // ----------------------------------------------------
    // PHASE 1: Customer registers, adds address, places COD order
    // ----------------------------------------------------
    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();

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
    await customerPage.locator('#addrPhone').fill('0912345678');
    await customerPage.locator('input[name="addressLine"]').fill('123 Đường Cầu Lông Đổi Trả');
    await customerPage.locator('input[name="province"]').fill('Hà Nội');
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click();
    await expect(customerPage.getByText('123 Đường Cầu Lông Đổi Trả')).toBeVisible();

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

    // Place COD order
    const checkoutBtn = customerPage.locator('.checkout-button');
    await expect(checkoutBtn).toBeEnabled();
    await checkoutBtn.click();

    const successCard = customerPage.locator('.checkout-success');
    await expect(successCard).toBeVisible({ timeout: 15_000 });
    const orderNumber = (await successCard.locator('h2').textContent())?.trim();
    expect(orderNumber, 'Order number should be generated').toBeTruthy();

    // ----------------------------------------------------
    // PHASE 2: Staff confirms COD & delivers shipment
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

    // Fulfillment steps
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
    await referenceInput.fill(`COD-PAY-${timestamp}`);

    const deliverBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận giao & thu COD' });
    await expect(deliverBtn).toBeVisible();
    await deliverBtn.click();
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công');

    // ----------------------------------------------------
    // PHASE 3: Customer requests return for delivered order
    // ----------------------------------------------------
    await customerPage.bringToFront();
    await customerPage.goto('/orders');
    await expect(customerPage.getByText(orderNumber!)).toBeVisible();

    const customerOrderCard = customerPage.locator('.customer-order-card', { hasText: orderNumber! });
    const returnBtn = customerOrderCard.getByRole('button', { name: /Yêu cầu đổi \/ trả hàng/ });
    await expect(returnBtn).toBeVisible();
    await returnBtn.click();

    // CreateReturnModal dialog appears
    const returnModal = customerPage.getByRole('dialog', { name: 'Yêu cầu đổi/trả hàng' });
    await expect(returnModal).toBeVisible();

    // Select DEFECTIVE reason
    await returnModal.locator('input[type="checkbox"]').first().check();
    await returnModal.locator('#return-reason').selectOption('DEFECTIVE');
    await returnModal.locator('#customer-note').fill('Hàng có lỗi sản xuất khi nhận.');

    // Submit return request
    await returnModal.getByRole('button', { name: 'Gửi yêu cầu đổi/trả' }).click();

    // Verify success alert and modal closed
    await expect(customerPage.getByText(/Yêu cầu đổi\/trả đã được gửi thành công/i)).toBeVisible();
    await expect(returnModal).not.toBeVisible();

    // Visit /account/returns to verify request status is REQUESTED
    await customerPage.goto('/account/returns');
    await expect(customerPage.getByText(`Đơn hàng #${orderNumber}`)).toBeVisible();
    await expect(customerPage.getByText('Chờ duyệt')).toBeVisible();

    // ----------------------------------------------------
    // PHASE 4: Admin approves the return request
    // ----------------------------------------------------
    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();

    await adminPage.goto('/login');
    await adminPage.locator('input[name="email"]').fill('admin@badminton.local');
    await adminPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await adminPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(adminPage).toHaveURL('/');

    await adminPage.goto('/admin');
    await adminPage.locator('#admin-tab-returns').click();
    await expect(adminPage.getByRole('heading', { name: 'Quản lý Đổi/Trả & Hoàn tiền' })).toBeVisible();

    const returnRow = adminPage.locator('tr', { hasText: orderNumber! });
    await expect(returnRow).toBeVisible();
    await expect(returnRow.getByText('Chờ duyệt')).toBeVisible();

    // Approve the request
    await returnRow.getByRole('button', { name: 'Duyệt' }).click();
    await expect(adminPage.getByRole('heading', { name: /Phê duyệt yêu cầu trả hàng/ })).toBeVisible();
    await adminPage.locator('.confirm-dialog button.button-primary, .confirm-dialog button:has-text("Xác nhận")').click();

    await expect(adminPage.getByText(new RegExp(`Đã phê duyệt yêu cầu cho đơn ${orderNumber}`, 'i'))).toBeVisible();
    await expect(returnRow.getByText('Đã duyệt')).toBeVisible();

    // ----------------------------------------------------
    // PHASE 5: Staff receives returned goods into quarantine and inspects them
    // ----------------------------------------------------
    await staffPage.bringToFront();
    await staffPage.reload();
    await staffPage.locator('#staff-tab-returns').click();
    await expect(staffPage.getByRole('heading', { name: 'Tiếp nhận & kiểm định hàng trả' })).toBeVisible();

    const staffReturnCard = staffPage.locator('.staff-return-card', { hasText: orderNumber! });
    await expect(staffReturnCard).toBeVisible();

    await staffReturnCard.getByRole('button', { name: 'Nhận vào khu cách ly' }).click();
    await expect(staffPage.getByRole('heading', { name: /Xác nhận nhận hàng hoàn/ })).toBeVisible();

    await staffPage.locator('#receive-evidence').fill('Biên bản kiểm tra: Hàng lỗi đúng mô tả, đã nhận lại đủ.');
    await staffPage.locator('.confirm-dialog button.button-primary, .confirm-dialog button:has-text("Xác nhận")').click();

    await expect(staffPage.getByText(new RegExp(`Đã nhận hàng đơn ${orderNumber} vào khu cách ly`, 'i'))).toBeVisible();
    await staffReturnCard.getByRole('button', { name: 'Kiểm định chất lượng' }).click();
    await expect(staffPage.getByRole('dialog', { name: 'Kiểm định hàng trả' })).toBeVisible();
    await staffPage.getByLabel('Ghi chú kiểm định').fill('Đủ điều kiện nhập lại kho bán hàng.');
    await staffPage.getByRole('button', { name: 'Hoàn tất kiểm định' }).click();
    await expect(staffPage.getByText(new RegExp(`Đã kiểm định hàng trả của đơn ${orderNumber}`, 'i'))).toBeVisible();
    await expect(staffReturnCard).not.toBeVisible();

    // ----------------------------------------------------
    // PHASE 6: Admin records manual refund
    // ----------------------------------------------------
    await adminPage.bringToFront();
    await adminPage.reload();
    await adminPage.locator('#admin-tab-returns').click();

    const receivedReturnRow = adminPage.locator('tr', { hasText: orderNumber! });
    await expect(receivedReturnRow).toBeVisible();
    await expect(receivedReturnRow.getByText('Đã kiểm định')).toBeVisible();

    // Open Manual Refund dialog
    await receivedReturnRow.getByRole('button', { name: 'Hoàn tiền' }).click();
    await expect(adminPage.getByRole('heading', { name: /Xác nhận hoàn tiền thủ công/ })).toBeVisible();

    await adminPage.locator('#evidence-reference').fill(refundEvidenceRef);
    await adminPage.locator('.confirm-dialog button.button-primary, .confirm-dialog button:has-text("Xác nhận")').click();

    await expect(adminPage.getByText(new RegExp(`Đã hoàn tiền cho yêu cầu trả hàng đơn ${orderNumber}`, 'i'))).toBeVisible();
    await expect(receivedReturnRow.getByText('Đã hoàn tiền')).toBeVisible();

    // ----------------------------------------------------
    // PHASE 7: Verification (Customer & Admin Reports)
    // ----------------------------------------------------
    // 1. Customer sees REFUNDED status and proof reference
    await customerPage.bringToFront();
    await customerPage.goto('/account/returns');
    const customerReturnCard = customerPage.locator('.customer-order-card', { hasText: orderNumber! });
    await expect(customerReturnCard).toBeVisible();
    await expect(customerReturnCard.getByText('Đã hoàn tiền')).toBeVisible();
    await expect(customerReturnCard.getByText(refundEvidenceRef)).toBeVisible();

    // 2. Admin report shows refund amount
    await adminPage.bringToFront();
    await adminPage.locator('#admin-tab-reports').click();
    await expect(adminPage.getByRole('heading', { name: 'Hiệu quả kinh doanh' })).toBeVisible();
    const refundMetric = adminPage.locator('.metric-card', { hasText: 'Tiền đã hoàn' });
    await expect(refundMetric).toBeVisible();
    await expect(refundMetric.locator('strong')).not.toHaveText(/^0\s*₫$/);
  });
});

