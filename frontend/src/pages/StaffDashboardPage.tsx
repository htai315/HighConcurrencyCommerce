import { useState, useEffect, useCallback } from 'react'
import { useLoaderData, useRevalidator } from 'react-router-dom'
import {
  type ManagedProduct,
  addStaffImage,
  addStaffSku,
  createStaffDraft,
  deleteStaffImage,
  updateStaffDraft,
  staffLoader,
} from '../features/product-management/product-management.api'
import {
  type SkuInventorySummary,
  type InventoryMovement,
  getInventorySummaries,
  getSkuMovements,
  stockIn,
  createAdjustmentRequest,
} from '../features/inventory/inventory.api'
import { formatPrice } from '../features/catalog/catalog.api'
import { CodOrdersPanel } from '../features/orders/CodOrdersPanel'
import { FulfillmentPanel } from '../features/fulfillment/FulfillmentPanel'
import { StaffTaskSummaryPanel } from '../features/reporting/StaffTaskSummaryPanel'

export function StaffDashboardPage() {
  const { products } = useLoaderData<typeof staffLoader>()
  const revalidator = useRevalidator()

  const [activeTab, setActiveTab] = useState<'products' | 'inventory' | 'cod' | 'tasks'>('products')

  // Product tab states
  const [selectedProductId, setSelectedProductId] = useState<string | null>(products[0]?.id ?? null)
  const [submitting, setSubmitting] = useState(false)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  // Form states for Draft creation
  const [newTitle, setNewTitle] = useState('')
  const [newDesc, setNewDesc] = useState('')

  // Form states for adding SKU
  const [skuCode, setSkuCode] = useState('')
  const [optionLabel, setOptionLabel] = useState('')

  // Form states for adding Image
  const [fileKey, setFileKey] = useState('')
  const [altText, setAltText] = useState('')
  const [displayOrder, setDisplayOrder] = useState('1')

  // Inventory tab states
  const [inventoryList, setInventoryList] = useState<SkuInventorySummary[]>([])
  const [loadingInventory, setLoadingInventory] = useState(false)
  const [stockInSkuId, setStockInSkuId] = useState<string>('')
  const [stockInQty, setStockInQty] = useState<string>('10')
  const [stockInSourceId, setStockInSourceId] = useState<string>(() => crypto.randomUUID())
  const [stockInNote, setStockInNote] = useState<string>('')
  const [adjustmentSkuId, setAdjustmentSkuId] = useState<string>('')
  const [adjustmentQty, setAdjustmentQty] = useState<string>('1')
  const [adjustmentReason, setAdjustmentReason] = useState<string>('')
  const [selectedMovementSku, setSelectedMovementSku] = useState<SkuInventorySummary | null>(null)
  const [movements, setMovements] = useState<InventoryMovement[]>([])
  const [loadingMovements, setLoadingMovements] = useState(false)

  const selectedProduct = products.find(p => p.id === selectedProductId) ?? null

  const refreshInventory = useCallback(async () => {
    setLoadingInventory(true)
    try {
      const data = await getInventorySummaries()
      setInventoryList(data)
      const first = data[0]
      if (first) {
        setStockInSkuId(prev => (prev ? prev : first.skuId))
        setAdjustmentSkuId(prev => (prev ? prev : first.skuId))
      }
    } catch {
      // Ignore or surface error
    } finally {
      setLoadingInventory(false)
    }
  }, [])

  useEffect(() => {
    let ignore = false
    if (activeTab === 'inventory') {
      getInventorySummaries()
        .then(data => {
          if (!ignore) {
            setInventoryList(data)
            const first = data[0]
            if (first) {
              setStockInSkuId(prev => (prev ? prev : first.skuId))
              setAdjustmentSkuId(prev => (prev ? prev : first.skuId))
            }
          }
        })
        .catch(() => {})
        .finally(() => {
          if (!ignore) setLoadingInventory(false)
        })
    }
    return () => {
      ignore = true
    }
  }, [activeTab])

  const handleCreateDraft = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newTitle.trim()) {
      setMessage({ type: 'error', text: 'Vui lòng nhập tiêu đề sản phẩm.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const created = await createStaffDraft({
        title: newTitle.trim(),
        description: newDesc.trim() || undefined,
      })
      setNewTitle('')
      setNewDesc('')
      setMessage({ type: 'success', text: `Tạo sản phẩm nháp "${created.title}" thành công.` })
      setSelectedProductId(created.id)
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Tạo sản phẩm nháp thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleUpdateDraft = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    if (!selectedProduct) return

    const form = new FormData(e.currentTarget)
    const title = ((form.get('title') as string) || '').trim()
    const description = ((form.get('description') as string) || '').trim()

    if (!title) {
      setMessage({ type: 'error', text: 'Tiêu đề sản phẩm không được để trống.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      await updateStaffDraft(selectedProduct.id, {
        title,
        description: description || undefined,
      })
      setMessage({ type: 'success', text: 'Cập nhật bản nháp thành công.' })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Cập nhật thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleAddSku = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!selectedProduct || !skuCode.trim() || !optionLabel.trim()) {
      setMessage({ type: 'error', text: 'Vui lòng nhập đầy đủ mã SKU và tên tùy chọn.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      await addStaffSku(selectedProduct.id, {
        skuCode: skuCode.trim(),
        optionLabel: optionLabel.trim(),
      })
      setSkuCode('')
      setOptionLabel('')
      setMessage({ type: 'success', text: 'Thêm biến thể SKU thành công.' })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Thêm biến thể SKU thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleAddImage = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!selectedProduct || !fileKey.trim()) {
      setMessage({ type: 'error', text: 'Vui lòng nhập đường dẫn/file key của ảnh.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      await addStaffImage(selectedProduct.id, {
        fileKey: fileKey.trim(),
        altText: altText.trim() || undefined,
        displayOrder: parseInt(displayOrder, 10) || 0,
      })
      setFileKey('')
      setAltText('')
      setDisplayOrder('1')
      setMessage({ type: 'success', text: 'Thêm ảnh thành công.' })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Thêm ảnh thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleDeleteImage = async (imageId: string) => {
    if (!selectedProduct) return
    setSubmitting(true)
    setMessage(null)
    try {
      await deleteStaffImage(selectedProduct.id, imageId)
      setMessage({ type: 'success', text: 'Xóa ảnh thành công.' })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Xóa ảnh thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleStockInSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const qty = parseInt(stockInQty, 10)
    if (!stockInSkuId || isNaN(qty) || qty <= 0) {
      setMessage({ type: 'error', text: 'Vui lòng chọn biến thể SKU và số lượng nhập lớn hơn 0.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const res = await stockIn({
        productSkuId: stockInSkuId,
        quantity: qty,
        sourceType: 'PURCHASE_RECEIPT',
        sourceId: stockInSourceId.trim() || undefined,
        note: stockInNote.trim() || undefined,
      })
      setMessage({
        type: 'success',
        text: `Nhập kho thành công: +${qty} sản phẩm. Tồn vật lý hiện tại: ${res.physicalQty}, Khả dụng: ${res.availableQty}.`,
      })
      setStockInNote('')
      setStockInSourceId(crypto.randomUUID())
      await refreshInventory()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Nhập kho thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleAdjustmentSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const quantity = parseInt(adjustmentQty, 10)
    if (!adjustmentSkuId || !Number.isSafeInteger(quantity) || quantity <= 0 || !adjustmentReason.trim()) {
      setMessage({ type: 'error', text: 'Vui lòng chọn SKU, nhập số lượng giảm và lý do kiểm kê.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const created = await createAdjustmentRequest({
        productSkuId: adjustmentSkuId,
        quantity,
        reason: adjustmentReason.trim(),
      })
      setMessage({
        type: 'success',
        text: `Đã gửi đề nghị giảm ${quantity} sản phẩm cho SKU ${created.skuCode}. Tồn kho chưa thay đổi và đang chờ Admin duyệt.`,
      })
      setAdjustmentQty('1')
      setAdjustmentReason('')
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Gửi đề nghị điều chỉnh thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleViewMovements = async (sku: SkuInventorySummary) => {
    setSelectedMovementSku(sku)
    setLoadingMovements(true)
    try {
      const list = await getSkuMovements(sku.skuId)
      setMovements(list)
    } catch {
      setMovements([])
    } finally {
      setLoadingMovements(false)
    }
  }

  return (
    <section className="content-page workspace-page">
      <p className="eyebrow">Back Office · Staff</p>
      {activeTab === 'products' ? <h1>Quản trị Sản phẩm & Media</h1>
        : activeTab === 'inventory' ? <h1>Vận hành Kho & Tồn kho</h1>
        : activeTab === 'cod' ? <h1>Xác nhận đơn COD</h1> : <h1>Tổng hợp tác vụ vận hành</h1>}
      <p className="page-intro">
        {activeTab === 'products'
          ? 'Nhân viên có thể tạo bản nháp sản phẩm (DRAFT), chỉnh sửa thông tin ban đầu, thêm biến thể SKU và quản lý hình ảnh.'
          : activeTab === 'inventory'
            ? 'Xem số lượng tồn kho khả dụng/vật lý của từng SKU, nhập kho bổ sung hàng và tra cứu lịch sử dịch chuyển (Movements).'
            : activeTab === 'cod' ? 'Duyệt hàng đợi COD trong hạn 24 giờ. Hệ thống tự nhả tồn và VOID payment khi quá hạn.'
              : 'Theo dõi nhanh các hàng đợi cần xử lý và cảnh báo tồn thấp.'}
      </p>

      <div className="dashboard-tabs" role="group" aria-label="Khu vực vận hành Staff">
        <button
          type="button"
          aria-pressed={activeTab === 'products'}
          className={activeTab === 'products' ? 'active' : ''}
          onClick={() => {
            setActiveTab('products')
            setMessage(null)
          }}
        >
          01 · Sản phẩm & Media
        </button>
        <button
          type="button"
          aria-label="🏭 Vận hành Kho & Tồn kho"
          aria-pressed={activeTab === 'inventory'}
          className={activeTab === 'inventory' ? 'active' : ''}
          onClick={() => {
            setActiveTab('inventory')
            setMessage(null)
          }}
        >
          02 · Vận hành Kho & Tồn kho
        </button>
        <button
          type="button"
          aria-pressed={activeTab === 'cod'}
          className={activeTab === 'cod' ? 'active' : ''}
          onClick={() => { setActiveTab('cod'); setMessage(null) }}
        >
          03 · Đơn COD chờ xác nhận
        </button>
        <button
          type="button"
          aria-pressed={activeTab === 'tasks'}
          className={activeTab === 'tasks' ? 'active' : ''}
          onClick={() => { setActiveTab('tasks'); setMessage(null) }}
        >
          04 · Tác vụ cần xử lý
        </button>
      </div>

      {message && (
        <div
          role="alert"
          style={{
            padding: '12px 18px',
            borderRadius: '12px',
            margin: '20px 0',
            backgroundColor: message.type === 'success' ? '#ecfdf5' : '#fef2f2',
            color: message.type === 'success' ? '#065f46' : '#991b1b',
            border: `1px solid ${message.type === 'success' ? '#a7f3d0' : '#fecaca'}`,
          }}
        >
          {message.text}
        </div>
      )}

      {activeTab === 'products' ? (
        <div className="management-layout">
          {/* Left Column: Form Create Draft & List of Products */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <div className="management-card">
              <h2>Tạo sản phẩm Draft mới</h2>
              <form onSubmit={handleCreateDraft}>
                <div className="form-field">
                  <label htmlFor="draft-title">Tên sản phẩm *</label>
                  <input
                    id="draft-title"
                    type="text"
                    placeholder="Ví dụ: Vợt Yonex Astrox 100ZZ"
                    value={newTitle}
                    onChange={e => setNewTitle(e.target.value)}
                    disabled={submitting}
                    required
                  />
                </div>
                <div className="form-field">
                  <label htmlFor="draft-desc">Mô tả tóm tắt</label>
                  <textarea
                    id="draft-desc"
                    rows={3}
                    style={{ width: '100%', padding: '10px 14px', border: '1px solid var(--line)', borderRadius: '12px' }}
                    placeholder="Thông tin công nghệ, thông số..."
                    value={newDesc}
                    onChange={e => setNewDesc(e.target.value)}
                    disabled={submitting}
                  />
                </div>
                <button type="submit" className="button-primary" style={{ width: '100%' }} disabled={submitting}>
                  {submitting ? 'Đang tạo...' : 'Tạo bản nháp'}
                </button>
              </form>
            </div>

            <div className="management-card">
              <h2>Danh sách sản phẩm ({products.length})</h2>
              <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '14px' }}>
                {products.length === 0 ? (
                  <p style={{ color: 'var(--muted)' }}>Chưa có sản phẩm nào. Hãy tạo bản nháp đầu tiên ở trên.</p>
                ) : (
                  products.map((p: ManagedProduct) => {
                    const isSelected = p.id === selectedProductId
                    return (
                      <button
                        key={p.id}
                        type="button"
                        onClick={() => setSelectedProductId(p.id)}
                        style={{
                          display: 'flex',
                          flexDirection: 'column',
                          alignItems: 'flex-start',
                          gap: '6px',
                          padding: '12px 14px',
                          borderRadius: '12px',
                          border: isSelected ? '2px solid var(--brand)' : '1px solid var(--line)',
                          backgroundColor: isSelected ? 'var(--paper-deep)' : 'white',
                          textAlign: 'left',
                          cursor: 'pointer',
                        }}
                      >
                        <div style={{ display: 'flex', justifyContent: 'space-between', width: '100%', alignItems: 'center' }}>
                          <strong style={{ fontSize: '15px' }}>{p.title}</strong>
                          <span className={`badge badge-${p.publicationStatus.toLowerCase()}`}>
                            {p.publicationStatus}
                          </span>
                        </div>
                        <span style={{ fontSize: '12px', color: 'var(--muted)' }}>
                          {p.skus.length} SKU · {p.images.length} ảnh
                        </span>
                      </button>
                    )
                  })
                )}
              </div>
            </div>
          </div>

          {/* Right Column: Manage Selected Product */}
          <div>
            {selectedProduct ? (
              <div className="management-card" style={{ display: 'flex', flexDirection: 'column', gap: '28px' }}>
                <div>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '12px' }}>
                    <h2 style={{ margin: 0 }}>{selectedProduct.title}</h2>
                    <span className={`badge badge-${selectedProduct.publicationStatus.toLowerCase()}`}>
                      {selectedProduct.publicationStatus}
                    </span>
                  </div>
                  <p style={{ color: 'var(--muted)', fontSize: '13px', marginTop: '6px' }}>
                    ID: {selectedProduct.id} · Tạo lúc {new Date(selectedProduct.createdAt).toLocaleDateString('vi-VN')}
                  </p>
                </div>

                {selectedProduct.publicationStatus === 'PUBLISHED' ? (
                  <div style={{ padding: '14px 18px', borderRadius: '12px', backgroundColor: '#ecfdf5', color: '#065f46', border: '1px solid #a7f3d0' }}>
                    ✓ Sản phẩm này đã được Admin công bố (PUBLISHED) lên Catalog công khai. Nhân viên không thể sửa đổi nội dung bản nháp.
                  </div>
                ) : (
                  <form key={selectedProduct.id} onSubmit={handleUpdateDraft} style={{ borderBottom: '1px solid var(--line)', paddingBottom: '24px' }}>
                    <h3>Chỉnh sửa nội dung bản nháp</h3>
                    <div className="form-field">
                      <label htmlFor="edit-title">Tiêu đề sản phẩm *</label>
                      <input
                        id="edit-title"
                        name="title"
                        type="text"
                        defaultValue={selectedProduct.title}
                        disabled={submitting}
                        required
                      />
                    </div>
                    <div className="form-field">
                      <label htmlFor="edit-desc">Mô tả sản phẩm</label>
                      <textarea
                        id="edit-desc"
                        name="description"
                        rows={3}
                        style={{ width: '100%', padding: '10px 14px', border: '1px solid var(--line)', borderRadius: '12px' }}
                        defaultValue={selectedProduct.description || ''}
                        disabled={submitting}
                      />
                    </div>
                    <button type="submit" className="button-secondary" disabled={submitting}>
                      {submitting ? 'Đang lưu...' : 'Lưu thông tin nháp'}
                    </button>
                  </form>
                )}

                {/* SKU Section */}
                <div style={{ borderBottom: '1px solid var(--line)', paddingBottom: '24px' }}>
                  <h3>Danh sách Biến thể SKU ({selectedProduct.skus.length})</h3>
                  <p style={{ fontSize: '13px', color: 'var(--muted)', marginTop: '4px' }}>
                    💡 Nhân viên chỉ thêm mã biến thể và thông số tùy chọn. Giá bán do Admin định mức trước khi duyệt.
                  </p>

                  <div className="table-responsive">
                    <table className="admin-table">
                      <thead>
                        <tr>
                          <th>Mã SKU</th>
                          <th>Tùy chọn</th>
                          <th>Giá hiện tại</th>
                          <th>Trạng thái</th>
                        </tr>
                      </thead>
                      <tbody>
                        {selectedProduct.skus.length === 0 ? (
                          <tr>
                            <td colSpan={4} style={{ textAlign: 'center', color: 'var(--muted)', padding: '20px' }}>
                              Chưa có SKU nào. Hãy thêm SKU để Admin có thể định giá và công bố sản phẩm.
                            </td>
                          </tr>
                        ) : (
                          selectedProduct.skus.map(s => (
                            <tr key={s.id}>
                              <td><strong>{s.skuCode}</strong></td>
                              <td>{s.optionLabel}</td>
                              <td>{s.regularPrice > 0 ? formatPrice(s.regularPrice) : <em style={{ color: 'var(--muted)' }}>Chưa đặt giá</em>}</td>
                              <td><span className="badge badge-published">{s.skuStatus}</span></td>
                            </tr>
                          ))
                        )}
                      </tbody>
                    </table>
                  </div>

                  {selectedProduct.publicationStatus === 'DRAFT' && (
                    <form onSubmit={handleAddSku} style={{ marginTop: '20px', padding: '16px', borderRadius: '14px', backgroundColor: 'var(--paper-deep)' }}>
                      <h4 style={{ margin: '0 0 12px' }}>Thêm biến thể SKU mới</h4>
                      <div className="form-row">
                        <div className="form-field">
                          <label htmlFor="new-sku-code">Mã SKU *</label>
                          <input
                            id="new-sku-code"
                            type="text"
                            placeholder="VD: ASTROX-4U-G5"
                            value={skuCode}
                            onChange={e => setSkuCode(e.target.value)}
                            disabled={submitting}
                            required
                          />
                        </div>
                        <div className="form-field">
                          <label htmlFor="new-sku-label">Tên phiên bản / Tùy chọn *</label>
                          <input
                            id="new-sku-label"
                            type="text"
                            placeholder="VD: Trọng lượng 4U, Cán G5"
                            value={optionLabel}
                            onChange={e => setOptionLabel(e.target.value)}
                            disabled={submitting}
                            required
                          />
                        </div>
                      </div>
                      <button type="submit" className="button-secondary" disabled={submitting}>
                        {submitting ? 'Đang thêm...' : '+ Thêm SKU'}
                      </button>
                    </form>
                  )}
                </div>

                {/* Media Section */}
                <div>
                  <h3>Hình ảnh sản phẩm Media ({selectedProduct.images.length})</h3>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '16px', marginTop: '16px' }}>
                    {selectedProduct.images.map(img => (
                      <div
                        key={img.id}
                        style={{
                          border: '1px solid var(--line)',
                          borderRadius: '12px',
                          overflow: 'hidden',
                          backgroundColor: 'var(--paper-deep)',
                          display: 'flex',
                          flexDirection: 'column',
                        }}
                      >
                        <div style={{ height: '110px', display: 'grid', placeItems: 'center', backgroundColor: '#e2e8f0' }}>
                          <img
                            src={img.fileKey.startsWith('http') || img.fileKey.startsWith('/') ? img.fileKey : `/${img.fileKey}`}
                            alt={img.altText || selectedProduct.title}
                            style={{ width: '100%', height: '100%', objectFit: 'cover' }}
                            onError={(e) => {
                              e.currentTarget.style.display = 'none'
                            }}
                          />
                        </div>
                        <div style={{ padding: '8px 10px', fontSize: '11px', flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
                          <div>
                            <strong style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {img.fileKey}
                            </strong>
                            <span style={{ color: 'var(--muted)' }}>Thứ tự: {img.displayOrder}</span>
                          </div>
                          {selectedProduct.publicationStatus === 'DRAFT' && (
                            <button
                              type="button"
                              onClick={() => handleDeleteImage(img.id)}
                              style={{
                                marginTop: '8px',
                                padding: '4px 8px',
                                border: '1px solid #fecaca',
                                borderRadius: '6px',
                                backgroundColor: '#fff1f2',
                                color: '#b91c1c',
                                fontSize: '11px',
                                cursor: 'pointer',
                              }}
                              disabled={submitting}
                            >
                              Xóa ảnh
                            </button>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>

                  {selectedProduct.publicationStatus === 'DRAFT' && (
                    <form onSubmit={handleAddImage} style={{ marginTop: '20px', padding: '16px', borderRadius: '14px', backgroundColor: 'var(--paper-deep)' }}>
                      <h4 style={{ margin: '0 0 12px' }}>Thêm hình ảnh cho sản phẩm</h4>
                      <div className="form-row">
                        <div className="form-field">
                          <label htmlFor="new-img-key">File Key / Đường dẫn ảnh *</label>
                          <input
                            id="new-img-key"
                            type="text"
                            placeholder="VD: images/astrox88d-front.webp"
                            value={fileKey}
                            onChange={e => setFileKey(e.target.value)}
                            disabled={submitting}
                            required
                          />
                        </div>
                        <div className="form-field">
                          <label htmlFor="new-img-alt">Mô tả ảnh (Alt text)</label>
                          <input
                            id="new-img-alt"
                            type="text"
                            placeholder="Mặt trước vợt cầu lông"
                            value={altText}
                            onChange={e => setAltText(e.target.value)}
                            disabled={submitting}
                          />
                        </div>
                      </div>
                      <div className="form-field" style={{ maxWidth: '200px' }}>
                        <label htmlFor="new-img-order">Thứ tự hiển thị</label>
                        <input
                          id="new-img-order"
                          type="number"
                          min="0"
                          value={displayOrder}
                          onChange={e => setDisplayOrder(e.target.value)}
                          disabled={submitting}
                        />
                      </div>
                      <button type="submit" className="button-secondary" disabled={submitting}>
                        {submitting ? 'Đang thêm...' : '+ Thêm ảnh'}
                      </button>
                    </form>
                  )}
                </div>
              </div>
            ) : (
              <div className="management-card" style={{ textAlign: 'center', padding: '60px 20px', color: 'var(--muted)' }}>
                <h3>Chưa chọn sản phẩm nào</h3>
                <p>Vui lòng chọn một sản phẩm từ danh sách hoặc tạo bản nháp mới ở cột bên trái.</p>
              </div>
            )}
          </div>
        </div>
      ) : activeTab === 'inventory' ? (
        /* Inventory Management Tab (M03) */
        <div style={{ display: 'flex', flexDirection: 'column', gap: '28px' }}>
          {/* Stock In Form Card */}
          <div className="management-card">
            <h2>Nhập tăng tồn kho (Stock-in)</h2>
            <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 16px' }}>
              Quy tắc bất biến: Tồn vật lý = Khả dụng + Đang giữ + Đã phân bổ. Thao tác nhập kho sẽ tăng đồng thời Tồn vật lý và Tồn khả dụng.
            </p>
            <form onSubmit={handleStockInSubmit}>
              <div className="form-row">
                <div className="form-field">
                  <label htmlFor="stock-sku-select">Chọn biến thể SKU *</label>
                  <select
                    id="stock-sku-select"
                    value={stockInSkuId}
                    onChange={e => setStockInSkuId(e.target.value)}
                    disabled={submitting || inventoryList.length === 0}
                    style={{ width: '100%', height: '48px', padding: '0 14px', borderRadius: '12px', border: '1px solid var(--line)' }}
                    required
                  >
                    {inventoryList.length === 0 ? (
                      <option value="">Chưa có biến thể SKU nào</option>
                    ) : (
                      inventoryList.map(s => (
                        <option key={s.skuId} value={s.skuId}>
                          {s.skuCode} — {s.productTitle} ({s.optionLabel})
                        </option>
                      ))
                    )}
                  </select>
                </div>
                <div className="form-field">
                  <label htmlFor="stock-qty-input">Số lượng nhập thêm *</label>
                  <input
                    id="stock-qty-input"
                    type="number"
                    min="1"
                    max="1000000"
                    placeholder="VD: 20"
                    value={stockInQty}
                    onChange={e => setStockInQty(e.target.value)}
                    disabled={submitting}
                    required
                  />
                </div>
              </div>
              <div className="form-field">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
                  <label htmlFor="stock-source-id" style={{ margin: 0 }}>Mã phiếu nhập (Source ID) *</label>
                  <button
                    type="button"
                    onClick={() => setStockInSourceId(crypto.randomUUID())}
                    disabled={submitting}
                    style={{ background: 'none', border: 'none', color: 'var(--brand)', cursor: 'pointer', fontSize: '12px', padding: 0 }}
                  >
                    🔄 Đổi mã mới
                  </button>
                </div>
                <input
                  id="stock-source-id"
                  type="text"
                  value={stockInSourceId}
                  onChange={e => setStockInSourceId(e.target.value)}
                  disabled={submitting}
                  required
                />
                <span style={{ fontSize: '11px', color: 'var(--muted)', marginTop: '4px', display: 'block' }}>
                  Khóa Idempotency chống cộng dồn trùng lặp khi mạng gián đoạn hoặc thử lại.
                </span>
              </div>
              <div className="form-field">
                <label htmlFor="stock-note-input">Ghi chú phiếu nhập</label>
                <input
                  id="stock-note-input"
                  type="text"
                  placeholder="VD: Lô hàng nhập từ nhà phân phối chính hãng Yonex Sunrise"
                  value={stockInNote}
                  onChange={e => setStockInNote(e.target.value)}
                  disabled={submitting}
                />
              </div>
              <button type="submit" className="button-primary" disabled={submitting || inventoryList.length === 0}>
                {submitting ? 'Đang xử lý nhập kho...' : '📥 Xác nhận nhập kho'}
              </button>
            </form>
          </div>

          <div className="management-card">
            <h2>Đề nghị giảm tồn kho</h2>
            <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 16px' }}>
              Staff chỉ tạo đề nghị. Tồn kho chưa thay đổi cho đến khi Admin duyệt; hệ thống không giảm phần Reserved hoặc Allocated.
            </p>
            <form onSubmit={handleAdjustmentSubmit}>
              <div className="form-row">
                <div className="form-field">
                  <label htmlFor="adjustment-sku-select">Chọn SKU cần giảm *</label>
                  <select
                    id="adjustment-sku-select"
                    value={adjustmentSkuId}
                    onChange={e => setAdjustmentSkuId(e.target.value)}
                    disabled={submitting || inventoryList.length === 0}
                    required
                    style={{ width: '100%', height: '48px', padding: '0 14px', borderRadius: '12px', border: '1px solid var(--line)' }}
                  >
                    {inventoryList.length === 0 ? <option value="">Chưa có biến thể SKU nào</option> : inventoryList.map(s => (
                      <option key={s.skuId} value={s.skuId}>
                        {s.skuCode} — khả dụng {s.availableQty}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="form-field">
                  <label htmlFor="adjustment-qty-input">Số lượng đề nghị giảm *</label>
                  <input
                    id="adjustment-qty-input"
                    type="number"
                    min="1"
                    max="1000000"
                    value={adjustmentQty}
                    onChange={e => setAdjustmentQty(e.target.value)}
                    disabled={submitting}
                    required
                  />
                </div>
              </div>
              <div className="form-field">
                <label htmlFor="adjustment-reason-input">Lý do điều chỉnh *</label>
                <input
                  id="adjustment-reason-input"
                  type="text"
                  maxLength={500}
                  placeholder="VD: Hàng hỏng sau kiểm kê định kỳ"
                  value={adjustmentReason}
                  onChange={e => setAdjustmentReason(e.target.value)}
                  disabled={submitting}
                  required
                />
              </div>
              <button type="submit" className="button-secondary" disabled={submitting || inventoryList.length === 0}>
                {submitting ? 'Đang gửi đề nghị...' : '📋 Gửi đề nghị chờ Admin duyệt'}
              </button>
            </form>
          </div>

          {/* Inventory Table Card */}
          <div className="management-card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '12px' }}>
              <div>
                <h2 style={{ margin: 0 }}>Bảng theo dõi Tồn kho ({inventoryList.length} SKU)</h2>
                <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 0' }}>
                  Đẳng thức phân vùng: <strong>Vật lý = Khả dụng + Đang giữ + Đã phân bổ</strong>
                </p>
              </div>
              <button
                type="button"
                className="button-secondary"
                onClick={() => void refreshInventory()}
                disabled={loadingInventory}
                style={{ padding: '8px 16px', minHeight: 'unset', fontSize: '13px' }}
              >
                {loadingInventory ? 'Đang làm mới...' : '🔄 Làm mới'}
              </button>
            </div>

            <div className="table-responsive">
              <table className="admin-table">
                <thead>
                  <tr>
                    <th>Mã SKU</th>
                    <th>Sản phẩm / Phân loại</th>
                    <th style={{ textAlign: 'right' }}>Vật lý (Physical)</th>
                    <th style={{ textAlign: 'right' }}>Khả dụng (Available)</th>
                    <th style={{ textAlign: 'right' }}>Đang giữ (Reserved)</th>
                    <th style={{ textAlign: 'right' }}>Đã phân bổ (Allocated)</th>
                    <th>Trạng thái</th>
                    <th style={{ textAlign: 'center' }}>Thao tác</th>
                  </tr>
                </thead>
                <tbody>
                  {inventoryList.length === 0 ? (
                    <tr>
                      <td colSpan={8} style={{ textAlign: 'center', padding: '30px', color: 'var(--muted)' }}>
                        {loadingInventory ? 'Đang tải dữ liệu kho...' : 'Chưa có biến thể SKU nào trong hệ thống.'}
                      </td>
                    </tr>
                  ) : (
                    inventoryList.map(item => (
                      <tr key={item.skuId}>
                        <td><strong>{item.skuCode}</strong></td>
                        <td>
                          <div>{item.productTitle}</div>
                          <span style={{ fontSize: '12px', color: 'var(--muted)' }}>{item.optionLabel}</span>
                        </td>
                        <td style={{ textAlign: 'right', fontWeight: 800, fontSize: '15px' }}>{item.physicalQty}</td>
                        <td style={{ textAlign: 'right', color: '#065f46', fontWeight: 700 }}>{item.availableQty}</td>
                        <td style={{ textAlign: 'right', color: '#b45309' }}>{item.reservedQty}</td>
                        <td style={{ textAlign: 'right', color: '#1d4ed8' }}>{item.allocatedQty}</td>
                        <td>
                          <span className={`badge badge-${item.publicationStatus.toLowerCase()}`}>
                            {item.publicationStatus}
                          </span>
                        </td>
                        <td style={{ textAlign: 'center' }}>
                          <div style={{ display: 'flex', gap: '8px', justifyContent: 'center' }}>
                            <button
                              type="button"
                              className="button-secondary"
                              onClick={() => {
                                setStockInSkuId(item.skuId)
                                window.scrollTo({ top: 0, behavior: 'smooth' })
                              }}
                              style={{ padding: '4px 10px', minHeight: 'unset', fontSize: '12px' }}
                            >
                              + Nhập
                            </button>
                            <button
                              type="button"
                              className="button-secondary"
                              onClick={() => void handleViewMovements(item)}
                              style={{ padding: '4px 10px', minHeight: 'unset', fontSize: '12px' }}
                            >
                              📜 Lịch sử
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>

          {/* Movements Ledger Panel */}
          {selectedMovementSku && (
            <div className="management-card" style={{ border: '2px solid var(--brand)', backgroundColor: 'var(--paper-deep)' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
                <div>
                  <h3 style={{ margin: 0 }}>
                    Sổ cái biến động kho: {selectedMovementSku.skuCode} ({selectedMovementSku.productTitle})
                  </h3>
                  <span style={{ fontSize: '13px', color: 'var(--muted)' }}>
                    Tổng số bản ghi movement: {movements.length}
                  </span>
                </div>
                <button
                  type="button"
                  className="button-secondary"
                  onClick={() => setSelectedMovementSku(null)}
                  style={{ padding: '4px 12px', minHeight: 'unset', fontSize: '12px' }}
                >
                  Đóng
                </button>
              </div>

              <div className="table-responsive">
                <table className="admin-table" style={{ backgroundColor: 'white', borderRadius: '12px' }}>
                  <thead>
                    <tr>
                      <th>Thời gian</th>
                      <th>Loại biến động</th>
                      <th style={{ textAlign: 'right' }}>Thay đổi (Delta)</th>
                      <th>Loại nguồn</th>
                      <th>Người thực hiện</th>
                      <th>Ghi chú</th>
                    </tr>
                  </thead>
                  <tbody>
                    {loadingMovements ? (
                      <tr>
                        <td colSpan={6} style={{ textAlign: 'center', padding: '20px' }}>Đang tải lịch sử...</td>
                      </tr>
                    ) : movements.length === 0 ? (
                      <tr>
                        <td colSpan={6} style={{ textAlign: 'center', padding: '20px', color: 'var(--muted)' }}>
                          Chưa có lịch sử biến động nào cho biến thể SKU này.
                        </td>
                      </tr>
                    ) : (
                      movements.map(m => (
                        <tr key={m.id}>
                          <td>{new Date(m.createdAt).toLocaleString('vi-VN')}</td>
                          <td>
                            <span className="badge badge-published">{m.movementType}</span>
                          </td>
                          <td style={{ textAlign: 'right', fontWeight: 700, color: m.qtyDelta > 0 ? '#065f46' : '#b91c1c' }}>
                            {m.qtyDelta > 0 ? `+${m.qtyDelta}` : m.qtyDelta}
                          </td>
                          <td><code>{m.sourceType}</code></td>
                          <td>{m.actorName || m.actorId || 'Hệ thống'}</td>
                          <td style={{ color: 'var(--muted)' }}>{m.note || '—'}</td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      ) : activeTab === 'cod' ? (
        <div style={{ display: 'grid', gap: '24px' }}><CodOrdersPanel /><FulfillmentPanel /></div>
      ) : <StaffTaskSummaryPanel />}
    </section>
  )
}
