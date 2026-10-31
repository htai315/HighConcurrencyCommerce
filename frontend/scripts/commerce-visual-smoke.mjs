import { chromium, expect } from '@playwright/test'
import { mkdir } from 'node:fs/promises'

// All API traffic is intercepted. These fixtures never reach or modify a database.
const base = process.env.VISUAL_SMOKE_URL ?? 'http://127.0.0.1:5173'
const output = new URL('../playwright-artifacts/', import.meta.url)
await mkdir(output, { recursive: true })
const now = '2026-10-31T03:00:00Z'
const user = { id: 'visual-user', fullName: 'Nguyễn Minh Anh', email: 'preview@example.invalid', role: 'CUSTOMER', phone: '0901234567' }
const address = { id: 'address', recipientName: user.fullName, phone: user.phone, addressLine: '123 Đường Thể Thao', ward: 'Phường 1', province: 'TP. Hồ Chí Minh', isDefault: true, createdAt: now }
const sku = { id: 'sku', productId: 'product', skuCode: 'HC-RACKET-4U', optionLabel: '4U / Xanh navy', regularPrice: 1590000, skuStatus: 'ACTIVE' }
const product = { id: 'product', title: 'Vợt cầu lông tốc độ HighCommerce', description: 'Thiết kế cân bằng, phù hợp cho những buổi tập và thi đấu phong trào.', categoryId: null, publicationStatus: 'DRAFT', createdAt: now, publishedAt: null, skus: [sku], images: [] }
const item = { productSkuId: sku.id, productId: product.id, productTitle: product.title, skuCode: sku.skuCode, optionLabel: sku.optionLabel, unitPrice: sku.regularPrice, quantity: 1, lineTotal: sku.regularPrice, imageUrl: null, availabilityCode: 'AVAILABLE' }
const order = { id: 'order', orderNumber: 'HC-20261031-001', orderStatus: 'PENDING_COD_CONFIRMATION', paymentMethod: 'COD', paymentStatus: 'COD_PENDING', reservationStatus: 'HELD', totalAmount: sku.regularPrice, createdAt: now, deadlineAt: '2026-11-01T03:00:00Z', confirmedAt: null, closedReason: null, recipientName: user.fullName, recipientPhone: user.phone, shippingAddress: `${address.addressLine}, ${address.province}`, shipmentId: null, shipmentStatus: null, shippedAt: null, deliveredAt: null, redeliveryCount: 0, canCancel: true, cancellationReason: null, items: [item], deliveryAttempts: [] }
const notification = { id: 'note', type: 'ORDER_CREATED', title: 'Đã tiếp nhận đơn hàng', body: 'Cửa hàng đang kiểm tra và xác nhận đơn hàng của bạn.', createdAt: now, readAt: null }
const inventory = { skuId: sku.id, skuCode: sku.skuCode, optionLabel: sku.optionLabel, productId: product.id, productTitle: product.title, physicalQty: 15, availableQty: 10, reservedQty: 3, allocatedQty: 2, skuStatus: 'ACTIVE', publicationStatus: 'DRAFT' }
const browser = await chromium.launch({ channel: 'msedge', headless: true })
try {
  for (const [device, viewport] of [['desktop', { width: 1440, height: 1000 }], ['tablet', { width: 820, height: 1180 }], ['mobile', { width: 390, height: 844 }]]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' })
    const page = await context.newPage()
    let role = 'CUSTOMER'
    let empty = false
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await page.route('**/api/v1/**', route => {
      const path = new URL(route.request().url()).pathname.replace('/api/v1', '')
      const responses = {
        '/auth/me': { ...user, role }, '/auth/csrf': { requestToken: 'visual-only' },
        '/account/profile': user, '/account/addresses': empty ? [] : [address],
        '/cart': { id: 'cart', items: empty ? [] : [item], totalQuantity: empty ? 0 : 1, subtotal: empty ? 0 : item.lineTotal, canCheckout: true, updatedAt: now },
        '/orders': { items: empty ? [] : [order], totalCount: empty ? 0 : 1, page: 1, pageSize: 50 },
        '/notifications': { items: empty ? [] : [notification], unreadCount: empty ? 0 : 1 },
        '/notifications/note/read': { ...notification, readAt: now },
        '/catalog/products/product': { ...product, category: null, skus: [{ ...sku, price: sku.regularPrice, inStock: true }], images: [] },
        '/catalog/flash-sales': [], '/staff/products': [product], '/admin/products': [product],
        '/staff/inventory': [inventory], '/staff/inventory/sku/movements': [],
        '/admin/inventory/adjustment-requests': [],
        '/staff/orders/cod-pending': [{ ...order, orderId: order.id, customerId: user.id, customerName: user.fullName, customerEmail: user.email }],
        '/staff/shipments': [{ ...order, orderId: order.id, customerName: user.fullName, returnStockCompleted: false, orderStatus: 'CONFIRMED' }],
        '/staff/dashboard/tasks': { pendingCodCount: 1, shipmentsToPackCount: 2, shipmentsToHandoverCount: 3, deliveryFailuresCount: 0, lowStockSkuCount: 1, notificationsUnreadCount: 1 },
        '/admin/reports/revenue': { from: '2026-10-01', to: '2026-10-31', revenue: 15900000, completedOrderCount: 10, averageOrderValue: 1590000, days: [], paymentMethods: [] },
        '/admin/reports/low-stock': { threshold: 10, items: [{ ...inventory, productSkuId: sku.id }] },
        '/admin/audit-logs': { items: [], page: 1, pageSize: 30, totalCount: 0 },
        '/admin/flash-sales': [],
      }
      if (path === '/catalog/products/missing') return route.fulfill({ status: 404, json: { code: 'NOT_FOUND' } })
      if (!(path in responses)) { errors.push(`Unhandled fixture: ${path}`); return route.fulfill({ status: 500, json: {} }) }
      return route.fulfill({ json: responses[path] })
    })
    async function capture(name) {
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
      await page.evaluate(() => document.fonts.ready)
      await page.screenshot({ path: new URL(`${name}-${device}.png`, output).pathname.replace(/^\/(\w:)/, '$1'), fullPage: true })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)
      expect(overflow, `${name} horizontal overflow`).toBe(false)
      expect(errors, name).toEqual([])
    }
    for (const [path, name] of [['/products/product', 'product-detail'], ['/cart', 'cart'], ['/account', 'account'], ['/account/security', 'security'], ['/orders', 'orders'], ['/notifications', 'notifications']]) {
      await page.goto(base + path)
      if (device === 'mobile' && name === 'product-detail') {
        await page.getByRole('button', { name: 'Tài khoản của bạn', exact: true }).click()
        await expect(page.getByRole('button', { name: 'Đăng xuất', exact: true })).toBeVisible()
        await page.keyboard.press('Escape')
        await expect(page.getByRole('button', { name: 'Đăng xuất', exact: true })).toBeHidden()
      }
      if (name === 'orders') await page.getByRole('button', { name: 'Xem chi tiết đơn' }).click()
      if (name === 'account') await page.getByRole('button', { name: 'Sửa', exact: true }).click()
      if (name === 'notifications') {
        await page.getByRole('button', { name: 'Đánh dấu đã đọc' }).click()
        await expect(page.getByText('Đã đọc', { exact: true })).toBeVisible()
      }
      await capture(name)
    }
    empty = true
    for (const path of ['/cart', '/orders', '/notifications']) { await page.goto(base + path); await capture(`${path.slice(1)}-empty`) }
    empty = false
    role = 'STAFF'
    await page.goto(base + '/staff')
    await capture('staff-products')
    for (const [label, name] of [['Vận hành Kho', 'inventory'], ['Đơn COD', 'cod'], ['Tác vụ', 'tasks']]) {
      await page.locator('.dashboard-tabs button').filter({ hasText: label }).click()
      await page.waitForLoadState('networkidle')
      await capture(`staff-${name}`)
    }
    role = 'ADMIN'
    await page.goto(base + '/admin')
    await capture('admin-products')
    for (const [label, name] of [['Duyệt giảm tồn', 'adjustments'], ['Báo cáo', 'reports'], ['Flash Sale', 'flash-sale']]) {
      await page.getByRole('tab', { name: new RegExp(label) }).click()
      await page.waitForLoadState('networkidle')
      if (name === 'flash-sale') await page.getByRole('button', { name: '+ Tạo chiến dịch mới', exact: true }).click()
      await capture(`admin-${name}`)
    }
    for (const [path, name] of [['/not-a-page', '404'], ['/products/missing', 'catalog-error'], ['/staff', 'forbidden']]) { await page.goto(base + path); await capture(name) }
    console.log(`${device}: customer pages, empty states, 8 dashboard tabs and 3 error views passed`)
    await context.close()
  }
} finally { await browser.close() }
