import { chromium, expect } from '@playwright/test'
import { mkdir } from 'node:fs/promises'

const baseUrl = process.env.AUTH_SMOKE_URL ?? 'http://localhost:5173'
const output = new URL('../playwright-artifacts/', import.meta.url)
await mkdir(output, { recursive: true })
const browser = await chromium.launch({ channel: 'msedge', headless: true })
try {
  for (const [device, viewport] of [['desktop', { width: 1440, height: 1000 }], ['tablet', { width: 820, height: 1180 }], ['mobile', { width: 390, height: 844 }]]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' })
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    for (const route of ['login', 'register']) {
      await page.goto(`${baseUrl}/${route}`)
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
      await page.evaluate(() => document.fonts.ready)
      await page.screenshot({ path: new URL(`${route}-${device}.png`, output).pathname.replace(/^\/(\w:)/, '$1'), fullPage: true })
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await expect(page.getByLabel('Email', { exact: true })).toBeVisible()
    }
    let registrationRequests = 0
    page.on('request', request => { if (request.url().endsWith('/auth/register')) registrationRequests++ })
    await page.getByLabel('Họ và tên').fill('Người chơi thử nghiệm')
    await page.getByLabel('Email', { exact: true }).fill('visual-test@example.invalid')
    await page.getByLabel('Mật khẩu', { exact: true }).fill('badminton1234')
    await page.getByLabel('Nhập lại mật khẩu', { exact: true }).fill('badminton1234')
    await page.getByRole('checkbox', { name: /Tôi đồng ý/ }).check()
    await page.getByRole('button', { name: 'Tạo tài khoản' }).click()
    await expect(page.getByRole('alert')).toContainText('chữ hoa')
    await page.getByLabel('Mật khẩu', { exact: true }).fill('Badminton123@')
    await page.getByRole('button', { name: 'Tạo tài khoản' }).click()
    await expect(page.getByRole('alert')).toContainText('chưa khớp')
    await page.getByRole('button', { name: 'Hiện mật khẩu', exact: true }).click()
    await expect(page.getByLabel('Mật khẩu', { exact: true })).toHaveAttribute('type', 'text')
    expect(registrationRequests).toBe(0)
    expect(errors).toEqual([])
    console.log(`${device}: screenshots, no overflow, password validation and visibility passed`)
    await context.close()
  }
} finally { await browser.close() }
