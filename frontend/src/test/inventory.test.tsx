import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { afterEach, expect, test } from 'vitest'
import { appRoutes } from '../App'
import { server } from './setup'
import type { SkuInventorySummary, InventoryMovement, StockInResult } from '../features/inventory/inventory.api'

type MockStockInBody = {
  productSkuId: string
  quantity: number
  sourceType?: string
  sourceId?: string
  note?: string
}

const authApi = 'http://localhost:5173/api/v1/auth'
const staffApi = 'http://localhost:5173/api/v1/staff'

const staffUser = { id: 'staff-1', email: 'staff@example.com', fullName: 'Staff Kho Vận', role: 'STAFF' }

let router: ReturnType<typeof createMemoryRouter> | undefined
afterEach(() => router?.dispose())

function open(path: string) {
  router = createMemoryRouter(appRoutes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

test('Staff: xem bảng tồn kho, nhập kho bổ sung hàng và xem lịch sử dịch chuyển', async () => {
  let inventoryState: SkuInventorySummary[] = [
    {
      skuId: '30000000-0000-0000-0000-000000000001',
      skuCode: 'ASTROX-100ZZ-4U',
      optionLabel: '4U - Kurenai',
      skuStatus: 'ACTIVE',
      productId: '20000000-0000-0000-0000-000000000001',
      productTitle: 'Vợt Cầu Lông Yonex Astrox 100ZZ',
      publicationStatus: 'PUBLISHED',
      physicalQty: 20,
      availableQty: 18,
      reservedQty: 2,
      allocatedQty: 0,
    },
    {
      skuId: '30000000-0000-0000-0000-000000000002',
      skuCode: 'NF1000Z-4UG5',
      optionLabel: '4U - G5 Lightning',
      skuStatus: 'ACTIVE',
      productId: '20000000-0000-0000-0000-000000000002',
      productTitle: 'Vợt Cầu Lông Yonex Nanoflare 1000Z',
      publicationStatus: 'PUBLISHED',
      physicalQty: 0,
      availableQty: 0,
      reservedQty: 0,
      allocatedQty: 0,
    },
  ]

  let movementsState: InventoryMovement[] = [
    {
      id: 'mov-1',
      productSkuId: '30000000-0000-0000-0000-000000000001',
      movementType: 'STOCK_IN',
      qtyDelta: 20,
      sourceType: 'PURCHASE_RECEIPT',
      sourceId: 'PO-2026-001',
      actorId: 'staff-1',
      actorName: 'Staff Kho Vận',
      note: 'Nhập lô hàng ban đầu',
      createdAt: '2026-10-30T09:00:00Z',
    },
  ]

  let lastStockInHeaderToken: string | null = null
  let lastStockInSourceId: string | null = null

  server.use(
    http.get(`${authApi}/me`, () => HttpResponse.json(staffUser)),
    http.get(`${authApi}/csrf`, () => HttpResponse.json({ requestToken: 'csrf-inv-token' })),
    http.get(`${staffApi}/products`, () => HttpResponse.json([])),
    http.get(`${staffApi}/inventory`, () => HttpResponse.json(inventoryState)),
    http.get(`${staffApi}/inventory/:skuId/movements`, ({ params }) => {
      const filtered = movementsState.filter(m => m.productSkuId === params.skuId)
      return HttpResponse.json(filtered)
    }),
    http.post(`${staffApi}/inventory/stock-in`, async ({ request }) => {
      lastStockInHeaderToken = request.headers.get('X-CSRF-TOKEN')
      const body = (await request.json()) as MockStockInBody
      lastStockInSourceId = body.sourceId ?? null
      const target = inventoryState.find(s => s.skuId === body.productSkuId)
      const newPhysical = (target?.physicalQty ?? 0) + body.quantity
      const newAvailable = (target?.availableQty ?? 0) + body.quantity

      // Cập nhật state mô phỏng DB
      inventoryState = inventoryState.map(s =>
        s.skuId === body.productSkuId
          ? { ...s, physicalQty: newPhysical, availableQty: newAvailable }
          : s,
      )

      const newMov: InventoryMovement = {
        id: `mov-${Date.now()}`,
        productSkuId: body.productSkuId,
        movementType: 'STOCK_IN',
        qtyDelta: body.quantity,
        sourceType: body.sourceType ?? 'PURCHASE_RECEIPT',
        sourceId: body.sourceId ?? 'PO-2026-TEST-002',
        actorId: 'staff-1',
        actorName: 'Staff Kho Vận',
        note: body.note ?? null,
        createdAt: new Date().toISOString(),
      }
      movementsState = [newMov, ...movementsState]

      const res: StockInResult = {
        balanceId: 'bal-mock-1',
        productSkuId: body.productSkuId,
        physicalQty: newPhysical,
        availableQty: newAvailable,
        reservedQty: target?.reservedQty ?? 0,
        allocatedQty: target?.allocatedQty ?? 0,
        revision: 1,
        movementId: newMov.id,
      }
      return HttpResponse.json(res, { status: 200 })
    }),
  )

  const user = userEvent.setup()
  open('/staff')

  // 1. Chuyển sang Tab Kho & Tồn kho
  const invTabBtn = await screen.findByRole('button', { name: /🏭 Vận hành Kho & Tồn kho/i })
  await user.click(invTabBtn)

  // Kiểm tra tiêu đề trang đổi thành Vận hành Kho & Tồn kho
  expect(await screen.findByRole('heading', { name: /Vận hành Kho & Tồn kho/i })).toBeVisible()

  // 2. Kiểm tra danh sách tồn kho hiển thị đầy đủ thông tin
  expect(await screen.findByText('ASTROX-100ZZ-4U')).toBeVisible()
  expect(screen.getByText('NF1000Z-4UG5')).toBeVisible()
  expect(screen.getByText('Vợt Cầu Lông Yonex Astrox 100ZZ')).toBeVisible()

  // 3. Thực hiện nhập kho cho NF1000Z-4UG5
  const skuSelect = screen.getByLabelText(/Chọn biến thể SKU \*/i)
  fireEvent.change(skuSelect, { target: { value: '30000000-0000-0000-0000-000000000002' } })

  const qtyInput = screen.getByLabelText(/Số lượng nhập thêm \*/i)
  fireEvent.change(qtyInput, { target: { value: '35' } })

  const noteInput = screen.getByLabelText(/Ghi chú phiếu nhập/i)
  fireEvent.change(noteInput, { target: { value: 'Lô hàng Nanoflare tuyển thủ' } })

  // Bấm Submit Nhập kho
  const submitBtn = screen.getByRole('button', { name: /Xác nhận nhập kho/i })
  await user.click(submitBtn)

  // 4. Xác nhận kết quả thành công, CSRF và Idempotency sourceId được gửi
  expect(await screen.findByText(/Nhập kho thành công: \+35 sản phẩm/i)).toBeVisible()
  expect(lastStockInHeaderToken).toBe('csrf-inv-token')
  expect(lastStockInSourceId).toBeTruthy()

  // Kiểm tra mã sourceId trên form được tự động đổi mới sau khi submit thành công
  const sourceIdInput = screen.getByLabelText(/Mã phiếu nhập \(Source ID\) \*/i) as HTMLInputElement
  expect(sourceIdInput.value).not.toBe(lastStockInSourceId)

  // 5. Kiểm tra bảng tồn kho đã tự động cập nhật số lượng mới (+35)
  await waitFor(() => {
    // Tìm hàng bảng của NF1000Z
    const row = screen.getByText('NF1000Z-4UG5').closest('tr')
    expect(row).not.toBeNull()
    expect(row?.textContent).toContain('35')
  })

  // 6. Mở xem sổ cái dịch chuyển (Movements Ledger) cho NF1000Z
  const nfRow = screen.getByText('NF1000Z-4UG5').closest('tr')!
  const viewHistoryBtn = nfRow.querySelector('button:last-child')!
  expect(viewHistoryBtn).not.toBeNull()
  await user.click(viewHistoryBtn)

  // Kiểm tra panel lịch sử xuất hiện
  expect(await screen.findByRole('heading', { name: /Sổ cái biến động kho: NF1000Z-4UG5/i })).toBeVisible()
  expect(await screen.findByText('+35')).toBeVisible()
  expect(screen.getByText('Lô hàng Nanoflare tuyển thủ')).toBeVisible()

  // Đóng panel
  const closeBtn = screen.getByRole('button', { name: /^Đóng$/i })
  await user.click(closeBtn)

  await waitFor(() => {
    expect(screen.queryByRole('heading', { name: /Sổ cái biến động kho: NF1000Z-4UG5/i })).toBeNull()
  })
})
