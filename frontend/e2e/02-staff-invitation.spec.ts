import { test, expect } from '@playwright/test';
import { waitForEmail, extractFragmentTokenFromText, clearAllEmails } from './helpers/mailpit';

test.describe('E2E Flow 2: Admin Staff Management & Staff Onboarding', () => {
  test.beforeEach(async () => {
    await clearAllEmails();
  });

  test('Admin invites a new Staff member, Staff activates account via Mailpit token, and accesses Staff Dashboard', async ({ browser, page }) => {
    const adminEmail = 'admin@badminton.local';
    const adminPassword = 'DemoPass123!@#';
    const staffEmail = `e2e_staff_${Date.now()}@badminton.local`;
    const staffFullName = 'Nguyễn E2E Staff';
    const staffPassword = 'StaffSecurePass123!@#';

    // 1. Admin login
    await page.goto('/login');
    await page.locator('input[name="email"]').fill(adminEmail);
    await page.locator('input[name="password"]').fill(adminPassword);
    await page.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(page).toHaveURL('/');

    // 2. Open Admin Dashboard
    await page.goto('/admin');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

    // 3. Switch to "Tài khoản & Nhân sự" tab
    const accountsTab = page.locator('#admin-tab-accounts');
    await accountsTab.click();
    await expect(page.getByText('Quản trị Tài khoản & Nhân sự')).toBeVisible();

    // 4. Open Invite Staff Modal
    await page.locator('[data-testid="admin-invite-staff-toggle-button"], button:has-text("Mời nhân sự")').click();

    // 5. Fill invite form
    await page.locator('input[name="inviteFullName"], [data-testid="admin-invite-staff-name-input"]').fill(staffFullName);
    await page.locator('input[name="inviteEmail"], [data-testid="admin-invite-staff-email-input"]').fill(staffEmail);
    await page.locator('[data-testid="admin-invite-staff-submit-button"], button:has-text("Gửi lời mời Staff")').click();

    // 6. Confirm success banner
    await expect(page.locator('.alert.alert-success, [role="alert"]')).toContainText(staffEmail);

    // 7. Verify email sent to Mailpit
    const emailMessage = await waitForEmail(staffEmail, { subject: 'Lời mời', timeoutMs: 20_000 });
    expect(emailMessage, 'Staff invitation email should be sent to Mailpit').not.toBeNull();
    expect(emailMessage!.Subject).toContain('Lời mời tham gia đội ngũ nhân sự (Staff)');

    const token = extractFragmentTokenFromText(emailMessage!.HTML || emailMessage!.Text);
    expect(token).toBeTruthy();

    // 8. Staff opens invitation in an isolated incognito browser context
    const staffContext = await browser.newContext();
    const staffPage = await staffContext.newPage();

    await staffPage.goto(`/staff-invitation#token=${token}`);
    await expect(staffPage.getByRole('heading', { name: 'Kích hoạt tài khoản nhân sự' })).toBeVisible();

    // Fragment stripped
    expect(staffPage.url()).not.toContain('#token=');

    // 9. Fill password
    await staffPage.locator('input[name="password"]').fill(staffPassword);
    await staffPage.locator('input[name="confirmPassword"]').fill(staffPassword);
    await staffPage.getByRole('button', { name: 'Kích hoạt tài khoản' }).click();

    // 10. Redirect to /login?staffInvited=1
    await expect(staffPage).toHaveURL(/.*login.*staffInvited=1/);
    await expect(staffPage.getByText('Tài khoản nhân sự đã kích hoạt thành công')).toBeVisible();

    // 11. Staff logs in
    await staffPage.locator('input[name="email"]').fill(staffEmail);
    await staffPage.locator('input[name="password"]').fill(staffPassword);
    await staffPage.getByRole('button', { name: 'Đăng nhập' }).click();
    await expect(staffPage).toHaveURL('/');

    // 12. Staff can access Staff Dashboard
    await staffPage.goto('/staff');
    await expect(staffPage.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(staffPage.getByText('Quản trị Sản phẩm & Media')).toBeVisible();

    await staffContext.close();
  });
});

