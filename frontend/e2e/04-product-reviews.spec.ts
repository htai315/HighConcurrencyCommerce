import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

test.describe('E2E Flow 4: Product Reviews Lifecycle & Moderation', () => {
  test('Complete reviews lifecycle: Purchase -> Review -> Edit -> Moderation (Hide/Restore) -> Soft-delete', async ({ browser }) => {
    // ----------------------------------------------------
    // PHASE 1: Customer registers, buys product via COD
    // ----------------------------------------------------
    const customerContext = await browser.newContext();
    const customerPage = await customerContext.newPage();

    const timestamp = Date.now();
    const buyerEmail = `e2e-reviewer-${timestamp}@badminton.local`;
    const buyerPassword = 'ReviewerPass123!@#';
    const buyerName = `Nguyễn Văn Đánh Giá ${timestamp}`;

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
    const loginLink = customerPage.locator('a[href="/login?verified=1"]');
    await expect(loginLink).toBeVisible();
    await loginLink.click();
    await expect(customerPage).toHaveURL(/.*login.*/);

    // 2. Login
    await customerPage.locator('input[name="email"]').fill(buyerEmail);
    await customerPage.locator('input[name="password"]').fill(buyerPassword);
    await customerPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(customerPage).toHaveURL('/');

    // 3. Add shipping address
    await customerPage.goto('/account');
    await expect(customerPage.getByRole('heading', { name: 'Hồ sơ & Sổ địa chỉ' })).toBeVisible();
    await customerPage.locator('details.address-create summary').click();
    await customerPage.locator('input[name="recipientName"]').fill(buyerName);
    await customerPage.locator('#addrPhone').fill('0909876543');
    await customerPage.locator('input[name="addressLine"]').fill('456 Đường Vợt Cầu Lông');
    await customerPage.locator('input[name="province"]').fill('Hồ Chí Minh');
    await customerPage.getByRole('button', { name: 'Thêm địa chỉ' }).click();
    await expect(customerPage.getByText('456 Đường Vợt Cầu Lông')).toBeVisible();

    // 4. Select product and add to cart
    await customerPage.goto('/');
    const productCard = customerPage.locator('a[href^="/products/"]').first();
    await expect(productCard).toBeVisible();
    await productCard.click();

    await expect(customerPage).toHaveURL(/.*products\/.+/);
    const productUrl = customerPage.url();

    const addToCartBtn = customerPage.getByRole('button', { name: 'Thêm vào giỏ' });
    await expect(addToCartBtn).toBeVisible();
    await addToCartBtn.click();
    await expect(customerPage.getByText('Đã thêm vào giỏ hàng.')).toBeVisible();

    // 5. Checkout COD
    await customerPage.goto('/cart');
    await expect(customerPage.getByRole('heading', { name: 'Giỏ hàng' })).toBeVisible();
    const checkoutBtn = customerPage.locator('.checkout-button, button:has-text("Đặt hàng")');
    await expect(checkoutBtn).toBeVisible();
    await checkoutBtn.click();

    const successCard = customerPage.locator('.checkout-success');
    await expect(successCard).toBeVisible({ timeout: 15_000 });
    const orderNumber = (await successCard.locator('h2').textContent())?.trim();
    expect(orderNumber, 'Order number should be generated').toBeTruthy();

    // ----------------------------------------------------
    // PHASE 2: Staff processes fulfillment to DELIVERED
    // ----------------------------------------------------
    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();

    await staffPage.goto('/login');
    await staffPage.locator('input[name="email"]').fill('staff@badminton.local');
    await staffPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await staffPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(staffPage).toHaveURL('/');

    await staffPage.goto('/staff');
    await expect(staffPage.getByRole('heading', { level: 1 })).toBeVisible();

    const codTab = staffPage.locator('#staff-tab-cod');
    await codTab.click();

    // Confirm COD order
    const codCard = staffPage.locator('.cod-order-card', { hasText: orderNumber! });
    await expect(codCard).toBeVisible({ timeout: 10_000 });
    const confirmCodBtn = codCard.getByRole('button', { name: 'Xác nhận đơn COD' });
    await expect(confirmCodBtn).toBeVisible();
    await confirmCodBtn.click();

    // Fulfill order
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
    await referenceInput.fill(`REF-REV-${Date.now()}`);

    const deliverBtn = fulfillmentRow.getByRole('button', { name: 'Xác nhận giao & thu COD' });
    await expect(deliverBtn).toBeVisible();
    await deliverBtn.click();
    await expect(staffPage.locator('.fulfillment-panel [role="alert"]')).toContainText('Giao thành công');

    // ----------------------------------------------------
    // PHASE 3: Customer submits product review
    // ----------------------------------------------------
    // Customer navigates via Orders page
    await customerPage.goto('/orders');
    await expect(customerPage.getByRole('heading', { name: 'Đơn hàng của tôi' })).toBeVisible();

    const writeReviewLink = customerPage.locator('a.write-review-order-link').first();
    await expect(writeReviewLink).toBeVisible();
    await writeReviewLink.click();

    // Land on PDP reviews section
    await expect(customerPage).toHaveURL(/.*products\/.*#reviews/);
    await expect(customerPage.locator('#reviews')).toBeVisible();

    // Open review form
    const openFormBtn = customerPage.locator('.open-review-form-btn');
    await expect(openFormBtn).toBeVisible();
    await openFormBtn.click();

    const reviewTextarea = customerPage.locator('textarea[name="reviewContent"]');
    await expect(reviewTextarea).toBeVisible();

    // Select 5 stars
    const fiveStarRadio = customerPage.getByRole('radio', { name: '5 trên 5 sao' });
    await fiveStarRadio.click();

    const reviewContent = 'Vợt cầu lông chất lượng cực kỳ tốt, cầm nhẹ tay và smash rất mạnh mẽ!';
    await reviewTextarea.fill(reviewContent);

    const submitReviewBtn = customerPage.locator('.submit-review-btn');
    await expect(submitReviewBtn).toBeEnabled();
    await submitReviewBtn.click();

    // Verify submission notice and review display
    await expect(customerPage.getByText('Cảm ơn bạn đã gửi đánh giá sản phẩm!')).toBeVisible({ timeout: 10_000 });
    await expect(customerPage.locator('.my-existing-review-box')).toBeVisible();
    await expect(customerPage.locator('.my-existing-review-box')).toContainText(reviewContent);

    // ----------------------------------------------------
    // PHASE 4: Customer edits review
    // ----------------------------------------------------
    const editReviewBtn = customerPage.locator('.my-existing-review-box .button-link.edit-review-btn, .my-existing-review-box button:has-text("Chỉnh sửa")');
    await expect(editReviewBtn).toBeVisible();
    await editReviewBtn.click();

    const updatedContent = 'Cập nhật sau 1 tuần: Vợt vẫn rất đầm tay và lưới căng chuẩn xác!';
    await customerPage.locator('textarea[name="reviewContent"]').fill(updatedContent);
    await customerPage.locator('.submit-review-btn').click();

    await expect(customerPage.getByText('Cập nhật đánh giá thành công!')).toBeVisible({ timeout: 10_000 });
    await expect(customerPage.locator('.my-existing-review-box')).toContainText(updatedContent);

    // ----------------------------------------------------
    // PHASE 5: Staff moderates review (Hide -> Restore)
    // ----------------------------------------------------
    await staffPage.goto('/staff');
    const staffReviewsTab = staffPage.locator('#staff-tab-reviews');
    await staffReviewsTab.click();

    await expect(staffPage.getByRole('heading', { name: 'Kiểm duyệt Đánh giá' })).toBeVisible();

    // Locate review row
    await staffPage.getByRole('searchbox', { name: 'Tìm kiếm đánh giá' }).fill(buyerEmail);
    const reviewRow = staffPage.locator('.moderation-table tbody tr', { hasText: buyerEmail });
    await expect(reviewRow).toBeVisible({ timeout: 10_000 });

    // Hide review with reason
    const hideBtn = reviewRow.locator('.btn-hide-review');
    await expect(hideBtn).toBeVisible();
    await hideBtn.click();

    const hideReasonTextarea = staffPage.locator('#hide-reason-input');
    await expect(hideReasonTextarea).toBeVisible();
    await hideReasonTextarea.fill('Tạm ẩn để kiểm tra nội dung phản hồi');

    const confirmHideBtn = staffPage.getByRole('button', { name: 'Xác nhận ẩn' });
    await expect(confirmHideBtn).toBeEnabled();
    await confirmHideBtn.click();

    await expect(staffPage.getByText(/Đã ẩn đánh giá/i)).toBeVisible({ timeout: 10_000 });
    const productId = new URL(productUrl).pathname.split('/').pop();
    const hiddenPage = await customerPage.request.get(`/api/v1/catalog/products/${productId}/reviews`);
    expect(hiddenPage.ok()).toBeTruthy();
    expect((await hiddenPage.json()).items.some((item: { content: string }) => item.content === updatedContent)).toBe(false);

    // Restore review
    const restoreBtn = reviewRow.locator('.btn-restore-review');
    await expect(restoreBtn).toBeVisible({ timeout: 10_000 });
    await restoreBtn.click();

    // Confirm dialog for restore
    const confirmRestoreBtn = staffPage.getByRole('dialog', { name: 'Khôi phục đánh giá sản phẩm' }).getByRole('button', { name: 'Khôi phục', exact: true });
    await expect(confirmRestoreBtn).toBeVisible();
    await confirmRestoreBtn.click();

    await expect(staffPage.getByText(/Đã khôi phục hiển thị đánh giá/i)).toBeVisible({ timeout: 10_000 });

    await staffContext.close();

    // ----------------------------------------------------
    // PHASE 6: Customer deletes review (Soft-delete)
    // ----------------------------------------------------
    await customerPage.goto(productUrl);
    await expect(customerPage.locator('#reviews')).toBeVisible();

    const deleteReviewBtn = customerPage.locator('.my-existing-review-box button:has-text("Xóa")');
    await expect(deleteReviewBtn).toBeVisible();
    await deleteReviewBtn.click();

    // Confirm delete in dialog
    const confirmDeleteBtn = customerPage.getByRole('button', { name: 'Xóa đánh giá' });
    await expect(confirmDeleteBtn).toBeVisible();
    await confirmDeleteBtn.click();

    await expect(customerPage.getByText('Đã xóa đánh giá của bạn.')).toBeVisible({ timeout: 10_000 });

    // After deletion, the customer is eligible to write a review again
    await expect(customerPage.locator('.open-review-form-btn')).toBeVisible({ timeout: 10_000 });

    await customerContext.close();
  });
});

