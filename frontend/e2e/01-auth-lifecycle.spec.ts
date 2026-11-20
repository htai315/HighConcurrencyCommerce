import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText, clearAllEmails } from './helpers/mailpit';

test.describe('E2E Flow 1: Authentication, Email Verification & Password Recovery', () => {
  test.beforeEach(async () => {
    await clearAllEmails();
  });

  test('Customer registers, verifies email via Mailpit link, and logs in', async ({ page }) => {
    const uniqueEmail = `e2e_customer_${Date.now()}@example.invalid`;
    const password = 'SecureE2EPass123!@#';
    const fullName = 'Nguyễn E2E Khách';

    // 1. Navigate to Register page
    await page.goto('/register');
    await expect(page.getByRole('heading', { name: 'Tạo tài khoản mới' })).toBeVisible();

    // 2. Fill registration form
    await page.locator('input[name="fullName"]').fill(fullName);
    await page.locator('input[name="email"]').fill(uniqueEmail);
    await page.locator('input[name="password"]').fill(password);
    await page.locator('input[name="confirmPassword"]').fill(password);
    await page.locator('input[type="checkbox"]').check();

    // 3. Submit registration
    await page.getByRole('button', { name: 'Tạo tài khoản' }).click();

    // 4. Verify redirected to /verify-email or notice shown
    await expect(page).toHaveURL(/.*verify-email.*registered=1/);

    // 5. Query Mailpit API to retrieve verification email
    const emailMessage = await waitForEmail(uniqueEmail, { subject: 'Xác minh địa chỉ email', timeoutMs: 20_000 });
    expect(emailMessage, 'Email should be delivered to Mailpit').not.toBeNull();
    expect(emailMessage!.Subject).toContain('Xác minh địa chỉ email');

    const token = extractFragmentTokenFromText(emailMessage!.HTML || emailMessage!.Text);
    expect(token, 'Token should be extracted from #token=... link in email').toBeTruthy();

    // 6. Navigate to verification link with fragment
    await page.goto(`/verify-email#token=${token}`);

    // Verify confirmation succeeds and shows success message or link to login
    await expect(page.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 });
    const loginLink = page.locator('a[href="/login?verified=1"]');
    await expect(loginLink).toBeVisible();
    await loginLink.click();

    // 7. Log in with newly verified credentials
    await expect(page).toHaveURL(/.*login.*/);
    await page.locator('input[name="email"]').fill(uniqueEmail);
    await page.locator('input[name="password"]').fill(password);
    await page.getByRole('button', { name: 'Đăng nhập' }).click();

    // 8. Confirm authenticated session on storefront
    await expect(page).toHaveURL('/');
    // Header should reflect authenticated session
    await expect(page.locator('.account-btn, .user-name, header').filter({ hasText: fullName })).toBeVisible();
  });

  test('Customer requests password reset, updates password via Mailpit, and logs in with new password', async ({ page }) => {
    // Register and verify a dedicated customer for this test so the shared demo account is NEVER mutated
    const timestamp = Date.now();
    const email = `e2e-reset-${timestamp}@badminton.local`;
    const initialPassword = 'InitialPass123!@#';
    const newPassword = 'NewResetPass456!@#';

    // 0. Quick registration & email verification of dedicated account
    await page.goto('/register');
    await page.locator('input[name="fullName"]').fill('Reset Test User');
    await page.locator('input[name="email"]').fill(email);
    await page.locator('input[name="password"]').fill(initialPassword);
    await page.locator('input[name="confirmPassword"]').fill(initialPassword);
    await page.locator('input[type="checkbox"]').check();
    await page.getByRole('button', { name: 'Tạo tài khoản' }).click();
    await expect(page).toHaveURL(/.*verify-email.*registered=1/);

    const regEmail = await waitForEmail(email, { subject: 'Xác minh địa chỉ email', timeoutMs: 15_000 });
    expect(regEmail).not.toBeNull();
    const verifyToken = extractFragmentTokenFromText(regEmail!.HTML || regEmail!.Text);
    expect(verifyToken).toBeTruthy();

    await page.goto(`/verify-email#token=${verifyToken}`);
    await expect(page.getByText('Xác minh thành công!')).toBeVisible({ timeout: 15_000 });
    const loginLink = page.locator('a[href="/login?verified=1"]');
    await expect(loginLink).toBeVisible();
    await loginLink.click();
    await expect(page).toHaveURL(/.*login.*/);

    // 1. Go to forgot password
    await page.goto('/forgot-password');
    await expect(page.getByRole('heading', { name: 'Quên mật khẩu' })).toBeVisible();

    await page.locator('input[name="email"]').fill(email);
    await page.getByRole('button', { name: 'Gửi liên kết' }).click();

    // Neutral 202 notice displayed
    await expect(page.locator('.auth-notice, [role="status"]')).toBeVisible();

    // 2. Fetch reset email from Mailpit
    const emailMessage = await waitForEmail(email, { subject: 'Đặt lại mật khẩu', timeoutMs: 20_000 });
    expect(emailMessage).not.toBeNull();
    expect(emailMessage!.Subject).toMatch(/đặt lại mật khẩu/i);

    const token = extractFragmentTokenFromText(emailMessage!.HTML || emailMessage!.Text);
    expect(token).toBeTruthy();

    // 3. Open reset password page with fragment
    await page.goto(`/reset-password#token=${token}`);
    await expect(page.getByRole('heading', { name: 'Đặt lại mật khẩu' })).toBeVisible();

    // Fragment should be stripped from URL immediately
    expect(page.url()).not.toContain('#token=');

    // 4. Fill new password
    await page.locator('input[name="newPassword"]').fill(newPassword);
    await page.locator('input[name="confirmPassword"]').fill(newPassword);
    await page.getByRole('button', { name: 'Lưu mật khẩu mới' }).click();

    // 5. Redirected to login with ?reset=1
    await expect(page).toHaveURL(/.*login.*reset=1/);
    await expect(page.getByText('Đặt lại mật khẩu thành công!')).toBeVisible();

    // 6. Log in with new password
    await page.locator('input[name="email"]').fill(email);
    await page.locator('input[name="password"]').fill(newPassword);
    await page.getByRole('button', { name: 'Đăng nhập' }).click();

    await expect(page).toHaveURL('/');
  });

  test('Google UI contract smoke: GIS container renders securely without exposing secrets when provider is enabled', async ({ page }) => {
    // 1. Verify backend providers endpoint contract
    const providersResponse = await page.request.get('/api/v1/auth/providers');
    expect(providersResponse.status()).toBe(200);
    const providersJson = await providersResponse.json();
    expect(providersJson).toHaveProperty('google');
    expect(providersJson.google).toHaveProperty('enabled');

    // 2. Intercept providers API to simulate configured Google Auth with an explicit Client ID
    const demoClientId = '1234567890-testdemo.apps.googleusercontent.com';
    await page.route('**/api/v1/auth/providers', async route => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          google: {
            enabled: true,
            clientId: demoClientId,
          },
        }),
      });
    });

    // 3. Navigate to login and verify GIS container renders with exact selectors
    await page.goto('/login');
    const googleWrapper = page.locator('.google-auth-wrapper, [data-testid="google-auth-wrapper"]');
    await expect(googleWrapper).toBeVisible();

    const googleBtnContainer = page.locator('.google-button-container, [data-testid="google-button-container"]');
    await expect(googleBtnContainer).toBeVisible();

    // 4. Confirm Client ID is passed into DOM container safely
    await expect(googleBtnContainer).toHaveAttribute('data-client-id', demoClientId);

    // 5. Verify NO secret leaks in rendered page content
    const content = await page.content();
    expect(content).toContain(demoClientId);
    expect(content).not.toContain('GOCSPX');
    expect(content).not.toContain('client_secret');
  });
});

