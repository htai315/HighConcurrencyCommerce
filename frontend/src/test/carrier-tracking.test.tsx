import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { AdminCarriersPanel } from '../features/carrier-tracking/AdminCarriersPanel'
import { CustomerTrackingPanel } from '../features/carrier-tracking/CustomerTrackingPanel'
import { StaffCarrierTrackingPanel } from '../features/carrier-tracking/StaffCarrierTrackingPanel'
import { parseCarrierConsignment } from '../features/carrier-tracking/carrier-tracking.api'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const carrier = {
  id: '11111111-1111-1111-1111-111111111111', code: 'GHN', name: 'Giao Hàng Nhanh', hotline: '1900636677',
  trackingUrlTemplate: 'https://tracking.example/{trackingNumber}', carrierStatus: 'ACTIVE', revision: 2,
  consignmentCount: 3, createdAt: '2026-11-07T01:00:00Z', updatedAt: '2026-11-07T02:00:00Z',
}
const consignment = {
  id: '22222222-2222-2222-2222-222222222222', targetType: 'SALES', targetShipmentId: '33333333-3333-3333-3333-333333333333',
  carrierId: carrier.id, carrierCode: 'GHN', carrierName: carrier.name, trackingNumber: 'GHN-2026-001',
  trackingUrl: 'https://tracking.example/GHN-2026-001', consignmentStatus: 'IN_TRANSIT', serviceCode: 'EXPRESS',
  recipientName: 'Nguyễn Văn A', recipientPhone: '0901234567', destination: '12 Nguyễn Trãi, TP.HCM',
  shippingMethodCode: 'EXPRESS', shippingMethodName: 'Giao hỏa tốc', label: { contentType: 'application/pdf', sizeBytes: 1024, sha256: 'abc123' },
  revision: 4, handedOverAt: '2026-11-07T03:00:00Z', deliveredAt: null, cancelledAt: null,
  createdAt: '2026-11-07T01:00:00Z', updatedAt: '2026-11-07T04:00:00Z',
  events: [{ id: '44444444-4444-4444-4444-444444444444', eventKey: 'picked-up', eventType: 'PICKED_UP', eventStatus: 'RECORDED', location: 'Kho TP.HCM', description: 'Đã lấy hàng', occurredAt: '2026-11-07T03:00:00Z', recordedAt: '2026-11-07T03:01:00Z', sourceType: 'STAFF' }],
}

test('Admin chỉnh sửa hãng bằng expectedRevision và nhận revision mới', async () => {
  let body: Record<string, unknown> | undefined
  server.use(
    http.get(`${root}/admin/carriers`, () => HttpResponse.json([carrier])),
    http.get(`${root}/admin/carriers/:id`, () => HttpResponse.json(carrier)),
    http.patch(`${root}/admin/carriers/:id`, async ({ request }) => { body = await request.json() as Record<string, unknown>; return HttpResponse.json({ ...carrier, name: 'GHN Express', revision: 3 }) }),
  )
  const user = userEvent.setup(); render(<AdminCarriersPanel />)
  expect(await screen.findByRole('heading', { name: carrier.name })).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Chỉnh sửa' }))
  const name = screen.getByLabelText('Tên hãng *'); await user.clear(name); await user.type(name, 'GHN Express')
  await user.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))
  await waitFor(() => expect(body).toMatchObject({ name: 'GHN Express', expectedRevision: 2 }))
  expect(await screen.findByRole('status')).toHaveTextContent('Đã cập nhật cấu hình hãng')
})

test('Staff tạo vận đơn qua CSRF với hãng ACTIVE', async () => {
  let body: Record<string, unknown> | undefined
  server.use(
    http.get(`${root}/staff/carrier-consignments/carriers`, () => HttpResponse.json([carrier])),
    http.get(`${root}/staff/carrier-consignments/queue`, () => HttpResponse.json([])),
    http.post(`${root}/staff/carrier-consignments/sales-shipments/:id`, async ({ request }) => { body = await request.json() as Record<string, unknown>; return HttpResponse.json({ ...consignment, consignmentStatus: 'DRAFT', label: null, events: [], revision: 1 }, { status: 201 }) }),
    http.get(`${root}/staff/carrier-consignments/:id`, () => HttpResponse.json({ ...consignment, consignmentStatus: 'DRAFT', label: null, events: [], revision: 1 })),
  )
  const user = userEvent.setup(); render(<StaffCarrierTrackingPanel />)
  expect(await screen.findByRole('option', { name: /Giao Hàng Nhanh/ })).toBeVisible()
  await user.type(screen.getByLabelText('Shipment ID *'), consignment.targetShipmentId)
  await user.type(screen.getByLabelText('Mã tracking *'), consignment.trackingNumber)
  await user.click(screen.getByRole('button', { name: 'Tạo vận đơn' }))
  await waitFor(() => expect(body).toMatchObject({ carrierId: carrier.id, trackingNumber: consignment.trackingNumber }))
  expect(await screen.findByRole('status')).toHaveTextContent('Hãy tải nhãn trước khi bàn giao')
})

test('Customer xem timeline và chỉ mở liên kết HTTPS an toàn', async () => {
  server.use(http.get(`${root}/orders/:id/tracking`, () => HttpResponse.json(consignment)))
  const user = userEvent.setup()
  render(<CustomerTrackingPanel orderId="order-1" summary={{ consignmentId: consignment.id, carrierCode: carrier.code, carrierName: carrier.name, trackingNumber: consignment.trackingNumber, trackingUrl: consignment.trackingUrl, consignmentStatus: consignment.consignmentStatus }} />)
  const external = screen.getByRole('link', { name: /Tra cứu trên website hãng/ })
  expect(external).toHaveAttribute('target', '_blank'); expect(external).toHaveAttribute('rel', 'noopener noreferrer')
  await user.click(screen.getByRole('button', { name: 'Xem hành trình' }))
  expect(await screen.findByText('Hãng đã lấy hàng')).toBeVisible()
  expect(screen.getByText('Kho TP.HCM · Đã lấy hàng')).toBeVisible()
})

test('parser từ chối trạng thái consignment ngoài contract', () => {
  expect(() => parseCarrierConsignment({ ...consignment, consignmentStatus: 'UNKNOWN' })).toThrow()
})
