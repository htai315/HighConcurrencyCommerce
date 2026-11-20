import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { AdminPayOsReconciliationPanel } from '../features/payments/AdminPayOsReconciliationPanel'
import type { ReconciliationDetail } from '../features/payments/reconciliation.api'
import { server } from './setup'

const root = 'http://localhost:5173/api/v1'
const caseId = '11111111-2222-3333-4444-555555555555'
const base: ReconciliationDetail = {
  id: caseId, paymentId: 'payment-1', orderId: 'order-1', orderNumber: 'HC-PAYOS-RECON-01',
  customerEmail: 'customer@example.invalid', customerName: 'Khách đối soát', customerId: 'customer-1',
  reason: 'LATE_PAID', caseStatus: 'OPEN', expectedAmount: 950000, paymentStatus: 'LATE_PAID',
  assignedTo: null, assignedToName: null, assignedAt: null, resolvedBy: null, resolvedByName: null,
  resolvedAt: null, note: null, createdAt: '2026-11-09T08:00:00Z', updatedAt: '2026-11-09T08:00:00Z',
  transactions: [{ id: 'transaction-1', providerTransactionId: 'PAYOS-SAFE-REF', amount: 950000, transactionStatus: 'RECONCILIATION_REQUIRED', providerPaidAt: '2026-11-09T08:00:00Z', verifiedAt: '2026-11-09T08:01:00Z' }],
  manualRefund: null,
}

test('Admin nhận case PayOS rồi ghi chứng từ hoàn tiền thủ công', async () => {
  let current = { ...base }
  let refundBody: unknown
  server.use(
    http.get(`${root}/admin/payos-reconciliation`, () => HttpResponse.json({ items: [current], page: 1, pageSize: 100, totalCount: 1 })),
    http.get(`${root}/admin/payos-reconciliation/${caseId}`, () => HttpResponse.json(current)),
    http.post(`${root}/admin/payos-reconciliation/${caseId}/claim`, ({ request }) => {
      if (request.headers.get('X-CSRF-TOKEN') !== 'csrf-test-token') return HttpResponse.json({}, { status: 403 })
      current = { ...current, caseStatus: 'IN_REVIEW', assignedTo: 'admin-1', assignedToName: 'Admin PayOS', assignedAt: '2026-11-09T08:05:00Z' }
      return HttpResponse.json(current)
    }),
    http.post(`${root}/admin/payos-reconciliation/${caseId}/manual-refund`, async ({ request }) => {
      refundBody = await request.json()
      current = { ...current, caseStatus: 'RESOLVED', paymentStatus: 'REFUNDED', note: 'Đã hoàn toàn bộ khoản tiền đến muộn.', manualRefund: { id: 'refund-1', transactionId: 'transaction-1', amount: 950000, evidenceReference: 'BANK-REF-001', refundStatus: 'REFUNDED', refundedAt: '2026-11-09T08:10:00Z', adminName: 'Admin PayOS' } }
      return HttpResponse.json(current)
    }),
  )

  render(<AdminPayOsReconciliationPanel />)
  const user = userEvent.setup()
  expect(await screen.findByText('HC-PAYOS-RECON-01')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Chi tiết' }))
  expect(await screen.findByText('PAYOS-SAFE-REF')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Nhận xử lý' }))
  await user.type(await screen.findByLabelText('Ghi chú quyết định'), 'Đã hoàn toàn bộ khoản tiền đến muộn.')
  await user.type(screen.getByLabelText('Mã chứng từ hoàn tiền'), 'BANK-REF-001')
  await user.click(screen.getByRole('button', { name: 'Ghi nhận hoàn tiền' }))
  expect(await screen.findByRole('dialog')).toBeVisible()
  await user.click(screen.getByRole('button', { name: 'Xác nhận đã hoàn tiền' }))

  expect(await screen.findByText(/Đã ghi nhận hoàn tiền thủ công/)).toBeVisible()
  expect(screen.getByText(/Chứng từ BANK-REF-001/)).toBeVisible()
  expect(refundBody).toEqual({ evidenceReference: 'BANK-REF-001', note: 'Đã hoàn toàn bộ khoản tiền đến muộn.' })
})
