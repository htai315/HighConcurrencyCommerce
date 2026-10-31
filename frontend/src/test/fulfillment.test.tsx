import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const staff = { id: 'staff-fulfillment', email: 'staff@example.invalid', fullName: 'Staff Fulfillment', role: 'STAFF' }
const baseOrder = {
  orderId: 'order-fulfillment-1', orderNumber: 'HC-20261030-F001', orderStatus: 'CONFIRMED',
  shipmentId: null as string | null, shipmentStatus: null as string | null, paymentStatus: 'COD_PENDING',
  customerName: 'Nguyễn Cầu Lông', totalAmount: 1250000, recipientName: 'Người nhận',
  recipientPhone: '0901234567', shippingAddress: '18 Đường Cầu, Hà Nội', shippedAt: null as string | null, deliveredAt: null as string | null,
  redeliveryCount: 0, returnStockCompleted: false,
  items: [{ productSkuId: 'sku-1', productTitle: 'Vợt High Pro', skuCode: 'HIGH-4U', optionLabel: '4U G5', quantity: 1 }],
}

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

test('Staff hoàn tất fulfillment COD qua bốn bước và gửi chứng từ thu tiền', async () => {
  let current = { ...baseOrder }
  let receivedDeliveryPayload: unknown
  const advance = (changes: Partial<typeof current>) => {
    current = { ...current, ...changes }
    return HttpResponse.json(current)
  }
  const validCsrf = (request: Request) => request.headers.get('X-CSRF-TOKEN') === 'csrf-test-token'

  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
    http.get(`${root}/staff/products`, () => HttpResponse.json([])),
    http.get(`${root}/staff/orders/cod-pending`, () => HttpResponse.json([])),
    http.get(`${root}/staff/shipments`, () => HttpResponse.json([current])),
    http.post(`${root}/staff/orders/:id/start-fulfillment`, ({ request }) => validCsrf(request)
      ? advance({ orderStatus: 'FULFILLING', shipmentId: 'shipment-1', shipmentStatus: 'PENDING_PACKING' })
      : HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })),
    http.post(`${root}/staff/shipments/:id/pack`, ({ request }) => validCsrf(request)
      ? advance({ shipmentStatus: 'PACKED' })
      : HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })),
    http.post(`${root}/staff/shipments/:id/handover`, ({ request }) => validCsrf(request)
      ? advance({ shipmentStatus: 'SHIPPING', shippedAt: '2026-10-30T08:00:00Z' })
      : HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })),
    http.post(`${root}/staff/shipments/:id/delivery-result`, async ({ request }) => {
      receivedDeliveryPayload = await request.json()
      return validCsrf(request)
        ? advance({ orderStatus: 'COMPLETED', shipmentStatus: 'DELIVERED', paymentStatus: 'COD_COLLECTED', deliveredAt: '2026-10-30T09:00:00Z' })
        : HttpResponse.json({ code: 'FORBIDDEN' }, { status: 403 })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Đơn COD chờ xác nhận/ }))

  expect(await screen.findByText(baseOrder.orderNumber)).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Tiếp nhận đơn' }))
  await user.click(await screen.findByRole('button', { name: 'Xác nhận đóng gói' }))
  await user.click(await screen.findByRole('button', { name: 'Bàn giao & xuất kho' }))

  const reference = await screen.findByRole('textbox', { name: 'Mã biên nhận thu COD' })
  await user.type(reference, 'COD-20261030-001')
  await user.click(screen.getByRole('button', { name: 'Xác nhận giao & thu COD' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('đã ghi nhận thu COD')
  expect(screen.queryByText(baseOrder.orderNumber)).toBeNull()
  expect(receivedDeliveryPayload).toEqual({
    result: 'DELIVERED', codCollectedAmount: 1250000, collectionReference: 'COD-20261030-001',
  })
})

test('Staff ghi giao thất bại, giao lại và hoàn kho với bằng chứng', async () => {
  let current = { ...baseOrder, orderStatus: 'FULFILLING', shipmentId: 'shipment-return-1', shipmentStatus: 'SHIPPING' }
  const deliveryPayloads: unknown[] = []
  let receivedReturnPayload: unknown
  const advance = (changes: Partial<typeof current>) => {
    current = { ...current, ...changes }
    return HttpResponse.json(current)
  }

  server.use(
    http.get(`${root}/auth/me`, () => HttpResponse.json(staff)),
    http.get(`${root}/staff/products`, () => HttpResponse.json([])),
    http.get(`${root}/staff/orders/cod-pending`, () => HttpResponse.json([])),
    http.get(`${root}/staff/shipments`, () => HttpResponse.json([current])),
    http.post(`${root}/staff/shipments/:id/delivery-result`, async ({ request }) => {
      deliveryPayloads.push(await request.json())
      return advance({ shipmentStatus: 'DELIVERY_FAILED' })
    }),
    http.post(`${root}/staff/shipments/:id/retry`, () => advance({ shipmentStatus: 'SHIPPING', redeliveryCount: 1 })),
    http.post(`${root}/staff/shipments/:id/return-stock`, async ({ request }) => {
      receivedReturnPayload = await request.json()
      return advance({ orderStatus: 'CANCELLED', returnStockCompleted: true })
    }),
  )

  router = createMemoryRouter(appRoutes, { initialEntries: ['/staff'] })
  render(<RouterProvider router={router} />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Đơn COD chờ xác nhận/ }))

  await user.type(await screen.findByRole('textbox', { name: 'Lý do giao thất bại' }), 'Khách vắng mặt')
  await user.click(screen.getByRole('button', { name: 'Ghi nhận giao thất bại' }))
  await user.click(await screen.findByRole('button', { name: 'Giao lại' }))
  expect(await screen.findByText('Đã bắt đầu lần giao lại 1/2.')).toBeVisible()

  const secondFailure = await screen.findByRole('textbox', { name: 'Lý do giao thất bại' })
  await user.clear(secondFailure)
  await user.type(secondFailure, 'Khách tiếp tục vắng mặt')
  await user.click(screen.getByRole('button', { name: 'Ghi nhận giao thất bại' }))
  await user.type(await screen.findByRole('textbox', { name: 'Bằng chứng hàng đã về kho' }), 'Biên bản BB-RETURN-01')
  await user.click(screen.getByRole('button', { name: 'Xác nhận hàng về & hoàn kho' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('hoàn kho đúng một lần')
  expect(screen.queryByText(baseOrder.orderNumber)).toBeNull()
  expect(deliveryPayloads).toEqual([
    { result: 'FAILED', failureReason: 'Khách vắng mặt' },
    { result: 'FAILED', failureReason: 'Khách tiếp tục vắng mặt' },
  ])
  expect(receivedReturnPayload).toEqual({ receivedEvidence: 'Biên bản BB-RETURN-01' })
})
