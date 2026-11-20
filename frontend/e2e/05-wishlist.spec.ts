import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText } from './helpers/mailpit';

test.describe('E2E Flow 5: Customer Wishlist Lifecycle', () => {
  test('Complete wishlist lifecycle: Guest redirect -> Register/Login -> Save from Catalog & PDP -> View /wishlist -> Add to Cart via Modal -> Remove from Wishlist', async ({ browser }) => {
    const customerContext = await browser.newContext();
    const page = await customerContext.newPage();

    // ----------------------------------------------------
    // PHASE 1: Guest click heart button redirects to Login
    // ----------------------------------------------------
    await page.goto('/');
    const firstHeartBtn = page.locator('button.wishlist-btn').first();
    await expect(firstHeartBtn).toBeVisible({ timeout: 10_000 });
    await expect(firstHeartBtn).toHaveAttribute('aria-pressed', 'false');

    await firstHeartBtn.click();
    await expect(page).toHaveURL(/.*login.*/);
    expect(new URL(page.url()).searchParams.get('returnTo')).toMatch(/^\/products\/[0-9a-f-]+$/i);

    // ----------------------------------------------------
    // PHASE 2: Register & verify email
    // ----------------------------------------------------
    const timestamp = Date.now();
    const email = `e2e-wishlist-${timestamp}@badminton.local`;
    const password = 'WishlistPass123!@#';
    const fullName = `Nguyễn Văn Yêu Thích ${timestamp}`;

    await page.goto('/register');
    await page.locator('input[name="fullName"]').fill(fullName);
    await page.locator('input[name="email"]').fill(email);
    await page.locator('input[name="password"]').fill(password);
    await page.locator('input[name="confirmPassword"]').fill(password);
    await page.locator('input[type="checkbox"]').check();
    await page.getByRole('button', { name: 'Tạo tài khoản' }).click();
    await expect(page).toHaveURL(/.*verify-email.*registered=1/);

    const regEmail = await waitForEmail(email, { subject: 'Xác minh địa chỉ email', timeoutMs: 15_000 });
    expect(regEmail, 'Verification email must be delivered').not.toBeNull();
    const verifyToken = extractFragmentTokenFromText(regEmail!.HTML || regEmail!.Text);
    expect(verifyToken, 'Verify token must be present').toBeTruthy();

    await page.goto(`/verify-email#token=${verifyToken}`);
    await expect(page.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 });
    const loginLink = page.locator('a[href="/login?verified=1"]');
    await expect(loginLink).toBeVisible();
    await loginLink.click();
    await expect(page).toHaveURL(/.*login.*/);

    // Login
    await page.locator('input[name="email"]').fill(email);
    await page.locator('input[name="password"]').fill(password);
    await page.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(page).toHaveURL('/');

    // ----------------------------------------------------
    // PHASE 3: Save product from Catalog
    // ----------------------------------------------------
    const catalogCard = page.locator('.product-card').first();
    await expect(catalogCard).toBeVisible();

    const productTitle = (await catalogCard.locator('h3 a').textContent())?.trim() ?? '';
    const heartBtn = catalogCard.locator('button.wishlist-btn');
    await expect(heartBtn).toHaveAttribute('aria-pressed', 'false');

    // Click heart to save
    await heartBtn.click();
    await expect(heartBtn).toHaveAttribute('aria-pressed', 'true');
    await expect(heartBtn).toHaveClass(/is-saved/);
    await expect(heartBtn).toBeEnabled(); // PUT has completed; safe to reload/navigate.

    // Header badge shows 1
    const wishlistHeaderLink = page.locator('a.shop-wishlist');
    await expect(wishlistHeaderLink).toBeVisible();
    await expect(wishlistHeaderLink.locator('.wishlist-count')).toHaveText('1');

    // The server-backed state survives a full reload.
    await page.reload();
    await expect(page.locator('.product-card').first().locator('button.wishlist-btn')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('a.shop-wishlist .wishlist-count')).toHaveText('1');

    // A different customer must never see this account's saved products.
    const otherContext = await browser.newContext();
    const otherPage = await otherContext.newPage();
    await otherPage.goto('/login');
    await otherPage.locator('input[name="email"]').fill('customer@badminton.local');
    await otherPage.locator('input[name="password"]').fill('DemoPass123!@#');
    await otherPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(otherPage).toHaveURL('/');
    await otherPage.goto('/wishlist');
    await expect(otherPage.getByText('Danh sách yêu thích đang trống')).toBeVisible();
    await otherContext.close();

    // ----------------------------------------------------
    // PHASE 4: Open PDP and verify Wishlist status
    // ----------------------------------------------------
    await catalogCard.locator('h3 a').click();
    await expect(page).toHaveURL(/.*products\/.*/);

    const pdpWishlistBtn = page.locator('.pdp-wishlist-btn');
    await expect(pdpWishlistBtn).toBeVisible();
    await expect(pdpWishlistBtn).toHaveAttribute('aria-pressed', 'true');
    await expect(pdpWishlistBtn).toContainText('Đã lưu yêu thích');

    // ----------------------------------------------------
    // PHASE 5: Navigate to /wishlist
    // ----------------------------------------------------
    await wishlistHeaderLink.click();
    await expect(page).toHaveURL(/.*wishlist/);

    await expect(page.getByRole('heading', { name: 'Sản phẩm yêu thích' })).toBeVisible();
    await expect(page.getByText(/1\/500 sản phẩm/)).toBeVisible();

    const wishlistCard = page.locator('.wishlist-card').first();
    await expect(wishlistCard).toBeVisible();
    await expect(wishlistCard.locator('.wishlist-card-title')).toContainText(productTitle);
    await expect(wishlistCard.locator('.wishlist-card-badge')).toContainText('Còn hàng');

    // ----------------------------------------------------
    // PHASE 6: Add to Cart via SkuSelectModal
    // ----------------------------------------------------
    const addToCartBtn = wishlistCard.locator('button.button-primary');
    await expect(addToCartBtn).toContainText('Thêm vào giỏ');
    await addToCartBtn.click();

    // Modal opens
    const modal = page.locator('.sku-modal-content');
    await expect(modal).toBeVisible();
    await expect(modal.getByRole('heading', { name: 'Chọn phiên bản' })).toBeVisible();

    // Select first SKU option if available
    const firstOption = modal.locator('.sku-option-btn:not(.out-of-stock)').first();
    await expect(firstOption).toBeVisible();
    const selectedOptionLabel = (await firstOption.locator('.sku-option-label').textContent())?.trim() ?? '';
    await firstOption.click();

    await modal.getByRole('button', { name: 'Tăng số lượng' }).click();
    await expect(modal.getByRole('spinbutton', { name: 'Số lượng' })).toHaveValue('2');

    const confirmAddBtn = modal.locator('button.button-primary');
    await confirmAddBtn.click();

    // Modal closes & success message shows
    await expect(modal).not.toBeVisible();
    await expect(page.locator('.wishlist-alert.success')).toBeVisible();
    await expect(page.locator('.wishlist-alert.success')).toContainText('Đã thêm');

    // Crucial rule: Product remains in wishlist!
    await expect(page.locator('.wishlist-card')).toHaveCount(1);

    // The cart contains the exact SKU and quantity selected in the modal.
    await page.getByRole('link', { name: /Xem giỏ hàng/ }).click();
    await expect(page).toHaveURL(/.*cart/);
    const cartItem = page.locator('.cart-item').first();
    await expect(cartItem).toContainText(selectedOptionLabel);
    await expect(cartItem.getByRole('spinbutton', { name: /Số lượng/ })).toHaveValue('2');
    await page.goto('/wishlist');
    await expect(page.locator('.wishlist-card')).toHaveCount(1);

    // ----------------------------------------------------
    // PHASE 7: Remove from Wishlist
    // ----------------------------------------------------
    const removeBtn = wishlistCard.locator('button.button-secondary');
    await expect(removeBtn).toContainText('Bỏ lưu');
    await removeBtn.click();

    // Empty state appears
    await expect(page.locator('.empty-state')).toBeVisible();
    await expect(page.getByText('Danh sách yêu thích đang trống')).toBeVisible();

    // Return to catalog and verify heart button is reset to false
    await page.goto('/');
    const catalogHeartBtn = page.locator('.product-card').first().locator('button.wishlist-btn');
    await expect(catalogHeartBtn).toHaveAttribute('aria-pressed', 'false');
  });
});

