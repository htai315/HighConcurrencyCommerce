import { chromium, expect } from '@playwright/test'

// Browser regression only: every API and GIS response is mocked, no real credentials.
const base = process.env.VISUAL_SMOKE_URL ?? 'http://127.0.0.1:5173'
const browser = await chromium.launch({ channel: 'msedge', headless: true })
try {
  for (const role of ['STAFF', 'ADMIN', 'CUSTOMER']) {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } })
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    let challenges = 0
    let methodRequests = 0
    let linked = false
    let linkAttempts = 0
    await page.route('https://accounts.google.com/gsi/client', route => route.fulfill({ contentType: 'application/javascript', body: `
      window.google = { accounts: { id: {
        initialize(config) { window.reviewGoogleConfig = config; },
        renderButton(container) { const button = document.createElement('button'); button.textContent = 'Google test'; button.onclick = () => window.reviewGoogleConfig.callback({credential: 'browser-test'}); container.appendChild(button); },
        cancel() {}
      } } };
    ` }))
    await page.route('**/api/v1/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (path.endsWith('/auth/me')) return route.fulfill({ json: { id: 'review', email: 'review@example.invalid', fullName: 'Review', role } })
      if (path.endsWith('/account/auth-methods')) {
        methodRequests++
        return role === 'CUSTOMER' ? route.fulfill({ json: { hasPassword: true, googleLinked: linked, googleEmail: linked ? 'review@example.invalid' : null } }) : route.fulfill({ status: 403, json: { code: 'FORBIDDEN' } })
      }
      if (path.endsWith('/auth/providers')) return route.fulfill({ json: { google: { enabled: true, clientId: 'review.apps.googleusercontent.com' } } })
      if (path.endsWith('/auth/csrf')) return route.fulfill({ json: { requestToken: 'test-csrf' } })
      if (path.endsWith('/google/challenge')) return route.fulfill({ json: { challengeId: `challenge-${++challenges}`, nonce: 'nonce' } })
      if (path.endsWith('/auth-methods/google')) {
        const body = route.request().postDataJSON()
        expect(body.currentPassword).toBe('CurrentPassword123!')
        expect(route.request().headers()['x-csrf-token']).toBe('test-csrf')
        if (++linkAttempts === 1) return route.fulfill({ status: 503, json: { code: 'GOOGLE_AUTH_UNAVAILABLE' } })
        expect(body.challengeId).toBe('challenge-2')
        linked = true
        return route.fulfill({ status: 204 })
      }
      if (path.endsWith('/cart')) return route.fulfill({ json: { id: null, items: [], totalQuantity: 0, subtotal: 0, canCheckout: false, updatedAt: null } })
      return route.fulfill({ json: [] })
    })
    await page.goto(base + '/account/security')
    await expect(page.locator('input[name="currentPassword"]')).toBeVisible()
    if (role !== 'CUSTOMER') {
      expect(methodRequests).toBe(0)
      await expect(page.getByRole('button', { name: 'Liên kết Google', exact: true })).toHaveCount(0)
    } else {
      await page.getByRole('button', { name: 'Liên kết Google', exact: true }).click()
      await expect(page.getByRole('button', { name: 'Google test' })).toBeVisible()
      await page.locator('input[name="linkPassword"]').pressSequentially('CurrentPassword123!', { delay: 25 })
      expect(challenges).toBe(1)
      await page.getByRole('button', { name: 'Google test' }).click()
      await page.getByRole('button', { name: 'Thử lại Google' }).click()
      await expect(page.getByRole('button', { name: 'Google test' })).toBeVisible()
      expect(challenges).toBe(2)
      await page.getByRole('button', { name: 'Google test' }).click()
      await expect(page.getByRole('dialog')).toHaveCount(0)
      await expect(page.getByText('Đã liên kết tài khoản Google thành công.')).toBeVisible()
    }
    expect(errors).toEqual([])
    console.log(`${role}: security form / SDK loading / stable challenge / retry checks passed`)
    await page.close()
  }
} finally { await browser.close() }
