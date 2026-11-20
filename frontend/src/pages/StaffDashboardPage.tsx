import { useState, useEffect, useCallback } from 'react'
import { useLoaderData, useRevalidator } from 'react-router-dom'
import { ConfirmDialog } from '../components/layout/ConfirmDialog'
import {
  type ManagedProduct,
  addStaffSku,
  createStaffDraft,
  deleteStaffImage,
  deleteStaffSpecification,
  upsertStaffSpecification,
  updateStaffDraft,
  uploadStaffImage,
  updateStaffImage,
  reorderStaffImages,
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
import { ReviewModerationPanel } from '../features/reviews/ReviewModerationPanel'
import { StaffReturnsPanel } from '../features/returns/StaffReturnsPanel'
import { ExchangeFulfillmentPanel } from '../features/returns/ExchangeFulfillmentPanel'
import { useWorkspaceSection } from '../components/layout/useWorkspaceSection'
import { StaffProductRevisionPanel } from '../features/product-revisions/StaffProductRevisionPanel'
import { StaffCarrierTrackingPanel } from '../features/carrier-tracking/StaffCarrierTrackingPanel'

export function StaffDashboardPage() {
  const { products, categories, brands } = useLoaderData<typeof staffLoader>()
  const revalidator = useRevalidator()
  const [productSearch, setProductSearch] = useState('')
  const [inventorySearch, setInventorySearch] = useState('')

  const [activeTab, setActiveTab] = useWorkspaceSection(['products', 'inventory', 'cod', 'carriers', 'tasks', 'reviews', 'returns'] as const, 'products')
  const [fulfillmentRefreshKey, setFulfillmentRefreshKey] = useState(0)

  // Product tab states
  const [selectedProductId, setSelectedProductId] = useState<string | null>(products[0]?.id ?? null)
  const [submitting, setSubmitting] = useState(false)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [deletingImage, setDeletingImage] = useState<{ id: string; fileKey: string } | null>(null)

  // Form states for Draft creation
  const [newTitle, setNewTitle] = useState('')
  const [newDesc, setNewDesc] = useState('')
  const [newCategoryId, setNewCategoryId] = useState('')
  const [newBrandId, setNewBrandId] = useState('')

  // Form states for adding SKU
  const [skuCode, setSkuCode] = useState('')
  const [optionLabel, setOptionLabel] = useState('')

  // Form states for adding Image
  const [uploadFiles, setUploadFiles] = useState<File[]>([])
  const [draggingMedia, setDraggingMedia] = useState(false)
  const [altText, setAltText] = useState('')
  const [displayOrder, setDisplayOrder] = useState('1')
  const [specKey, setSpecKey] = useState('')
  const [specName, setSpecName] = useState('')
  const [specValue, setSpecValue] = useState('')
  const [specOrder, setSpecOrder] = useState('1')

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
  const visibleProducts = products.filter(p => `${p.title} ${p.skus.map(sku => sku.skuCode).join(' ')}`.toLocaleLowerCase('vi').includes(productSearch.trim().toLocaleLowerCase('vi')))
  const visibleInventory = inventoryList.filter(item => `${item.skuCode} ${item.productTitle} ${item.optionLabel}`.toLocaleLowerCase('vi').includes(inventorySearch.trim().toLocaleLowerCase('vi')))

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
        categoryId: newCategoryId || undefined,
        brandId: newBrandId || undefined,
      })
      setNewTitle('')
      setNewDesc('')
      setNewCategoryId('')
      setNewBrandId('')
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
    const categoryId = ((form.get('categoryId') as string) || '').trim()
    const brandId = ((form.get('brandId') as string) || '').trim()

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
        categoryId: categoryId || undefined,
        brandId: brandId || undefined,
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
    if (!selectedProduct || uploadFiles.length === 0) {
      setMessage({ type: 'error', text: 'Vui lòng chọn ít nhất một ảnh JPEG, PNG hoặc WebP.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const baseOrder = parseInt(displayOrder, 10) || 0
      for (const [index, file] of uploadFiles.entries()) {
        await uploadStaffImage(selectedProduct.id, file, {
          altText: altText.trim() ? `${altText.trim()}${uploadFiles.length > 1 ? ` — ${file.name}` : ''}` : undefined,
          displayOrder: baseOrder + index,
        })
      }
      setUploadFiles([])
      setAltText('')
      setDisplayOrder('1')
      setMessage({ type: 'success', text: `Đã upload ${uploadFiles.length} ảnh thành công.` })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Thêm ảnh thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const chooseUploadFiles = (files: File[]) => {
    const accepted = files.filter(file => ['image/jpeg', 'image/png', 'image/webp'].includes(file.type) && file.size > 0 && file.size <= 8 * 1024 * 1024)
    setUploadFiles(accepted)
    setMessage(accepted.length === files.length ? null : { type: 'error', text: 'Một số tệp bị bỏ qua: chỉ nhận JPEG/PNG/WebP, tối đa 8 MiB mỗi ảnh.' })
  }

  const handleUpdateImage = async (imageId: string, form: HTMLFormElement) => {
    if (!selectedProduct) return
    const data = new FormData(form)
    setSubmitting(true); setMessage(null)
    try {
      await updateStaffImage(selectedProduct.id, imageId, { altText: String(data.get('altText') ?? '').trim() || undefined, displayOrder: Number.parseInt(String(data.get('displayOrder') ?? '0'), 10) || 0 })
      setMessage({ type: 'success', text: 'Đã cập nhật metadata ảnh.' }); revalidator.revalidate()
    } catch (err) { setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể cập nhật ảnh.' }) }
    finally { setSubmitting(false) }
  }

  const handleMoveImage = async (imageId: string, direction: -1 | 1) => {
    if (!selectedProduct) return
    const ordered = [...selectedProduct.images].sort((a, b) => a.displayOrder - b.displayOrder || a.id.localeCompare(b.id))
    const current = ordered.findIndex(image => image.id === imageId); const target = current + direction
    if (current < 0 || target < 0 || target >= ordered.length) return
    ;[ordered[current], ordered[target]] = [ordered[target]!, ordered[current]!]
    setSubmitting(true); setMessage(null)
    try { await reorderStaffImages(selectedProduct.id, ordered.map(image => image.id)); setMessage({ type: 'success', text: 'Đã sắp xếp lại gallery.' }); revalidator.revalidate() }
    catch (err) { setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể sắp xếp gallery.' }) }
    finally { setSubmitting(false) }
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

  const handleUpsertSpecification = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!selectedProduct || !/^[a-z0-9]+(?:_[a-z0-9]+)*$/.test(specKey) || !specName.trim() || !specValue.trim()) {
      setMessage({ type: 'error', text: 'Khóa thông số dùng snake_case; tên và giá trị không được trống.' }); return
    }
    setSubmitting(true); setMessage(null)
    try {
      await upsertStaffSpecification(selectedProduct.id, specKey, { displayName: specName.trim(), value: specValue.trim(), displayOrder: Number.parseInt(specOrder, 10) || 0 })
      setSpecKey(''); setSpecName(''); setSpecValue(''); setSpecOrder('1'); setMessage({ type: 'success', text: 'Đã lưu thông số kỹ thuật.' }); revalidator.revalidate()
    } catch (err) { setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể lưu thông số.' }) }
    finally { setSubmitting(false) }
  }

  const handleDeleteSpecification = async (key: string) => {
    if (!selectedProduct) return
    setSubmitting(true); setMessage(null)
    try { await deleteStaffSpecification(selectedProduct.id, key); setMessage({ type: 'success', text: 'Đã xóa thông số.' }); revalidator.revalidate() }
    catch (err) { setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể xóa thông số.' }) }
    finally { setSubmitting(false) }
  }

  const handleStockInSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const qty = parseInt(stockInQty, 10)
    const sourceId = stockInSourceId.trim()
    if (!stockInSkuId || isNaN(qty) || qty <= 0 || !sourceId) {
      setMessage({ type: 'error', text: 'Vui lòng chọn SKU, nhập số lượng lớn hơn 0 và cung cấp mã phiếu nhập.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const res = await stockIn({
        productSkuId: stockInSkuId,
        quantity: qty,
        sourceType: 'PURCHASE_RECEIPT',
        sourceId,
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
      {deletingImage && (
        <ConfirmDialog
          title="Xóa ảnh sản phẩm?"
          description={`Bạn có chắc chắn muốn xóa ảnh "${deletingImage.fileKey}"? Thao tác này không thể hoàn tác.`}
          confirmLabel="Xác nhận xóa"
          busy={submitting}
          onCancel={() => setDeletingImage(null)}
          onConfirm={async () => {
            await handleDeleteImage(deletingImage.id)
            setDeletingImage(null)
          }}
        />
      )}
      <p className="eyebrow">Back Office · Staff</p>
      {activeTab === 'products' ? <h1>Quản trị Sản phẩm & Media</h1>
        : activeTab === 'inventory' ? <h1>Vận hành Kho & Tồn kho</h1>
        : activeTab === 'cod' ? <h1>Xác nhận đơn COD</h1>
        : activeTab === 'carriers' ? <h1>Vận đơn & Tracking</h1>
        : activeTab === 'reviews' ? <h1>Kiểm duyệt Đánh giá</h1>
        : activeTab === 'returns' ? <h1>Tiếp nhận Hàng đổi/trả</h1>
        : <h1>Tổng hợp tác vụ vận hành</h1>}
      <p className="page-intro">
        {activeTab === 'products'
          ? 'Nhân viên có thể tạo bản nháp sản phẩm (DRAFT), chỉnh sửa thông tin ban đầu, thêm biến thể SKU và quản lý hình ảnh.'
          : activeTab === 'inventory'
            ? 'Xem số lượng tồn kho khả dụng/vật lý của từng SKU, nhập kho bổ sung hàng và tra cứu lịch sử dịch chuyển (Movements).'
            : activeTab === 'cod' ? 'Duyệt hàng đợi COD trong hạn 24 giờ. Hệ thống tự nhả tồn và VOID payment khi quá hạn.'
            : activeTab === 'carriers' ? 'Tạo vận đơn từ shipment, tải nhãn, bàn giao hãng và cập nhật hành trình giao nhận.'
              : activeTab === 'reviews'
                ? 'Kiểm duyệt nhận xét của khách hàng, ẩn các nội dung vi phạm tiêu chuẩn và khôi phục khi cần thiết.'
                : activeTab === 'returns'
                  ? 'Kiểm tra hàng trả và hoàn kho đúng một lần.'
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
          Sản phẩm & Media
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
          Vận hành Kho & Tồn kho
        </button>
        <button
          type="button"
          id="staff-tab-cod"
          data-testid="staff-tab-cod"
          aria-pressed={activeTab === 'cod'}
          className={activeTab === 'cod' ? 'active' : ''}
          onClick={() => { setActiveTab('cod'); setMessage(null) }}
        >
          Đơn COD chờ xác nhận
        </button>
        <button
          type="button"
          id="staff-tab-returns"
          data-testid="staff-tab-returns"
          aria-pressed={activeTab === 'returns'}
          className={activeTab === 'returns' ? 'active' : ''}
          onClick={() => { setActiveTab('returns'); setMessage(null) }}
        >
          Nhận hàng trả
        </button>
        <button
          type="button"
          id="staff-tab-carriers"
          data-testid="staff-tab-carriers"
          aria-pressed={activeTab === 'carriers'}
          className={activeTab === 'carriers' ? 'active' : ''}
          onClick={() => { setActiveTab('carriers'); setMessage(null) }}
        >
          Vận đơn &amp; Tracking
        </button>
        <button
          type="button"
          aria-pressed={activeTab === 'tasks'}
          className={activeTab === 'tasks' ? 'active' : ''}
          onClick={() => { setActiveTab('tasks'); setMessage(null) }}
        >
          Tác vụ cần xử lý
        </button>
        <button
          type="button"
          id="staff-tab-reviews"
          data-testid="staff-tab-reviews"
          aria-pressed={activeTab === 'reviews'}
          className={activeTab === 'reviews' ? 'active' : ''}
          onClick={() => { setActiveTab('reviews'); setMessage(null) }}
        >
          Kiểm duyệt Đánh giá
        </button>
      </div>

      {message && (
        <div
          role="alert"
          className={`alert ${message.type === 'success' ? 'success' : 'error'}`}
          style={{ margin: '20px 0' }}
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
                  <label htmlFor="draft-brand">Thương hiệu</label>
                  <select id="draft-brand" value={newBrandId} onChange={event => setNewBrandId(event.target.value)} disabled={submitting}>
                    <option value="">Chưa chọn thương hiệu</option>
                    {brands.filter(brand => brand.isActive).map(brand => <option key={brand.id} value={brand.id}>{brand.name}</option>)}
                  </select>
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
                <div className="form-field">
                  <label htmlFor="draft-category">Danh mục</label>
                  <select id="draft-category" value={newCategoryId} onChange={event => setNewCategoryId(event.target.value)} disabled={submitting}>
                    <option value="">Chưa phân loại</option>
                    {categories.filter(category => category.isVisible).map(category => <option key={category.id} value={category.id}>{category.name}</option>)}
                  </select>
                </div>
                <button type="submit" className="button-primary" style={{ width: '100%' }} disabled={submitting}>
                  {submitting ? 'Đang tạo...' : 'Tạo bản nháp'}
                </button>
              </form>
            </div>

            <div className="management-card">
              <h2>Danh sách sản phẩm ({products.length})</h2>
              <label className="workspace-search">Tìm sản phẩm<input type="search" value={productSearch} onChange={event => setProductSearch(event.target.value)} placeholder="Tên sản phẩm hoặc mã SKU" /></label>
              {products.length > 0 && visibleProducts.length === 0 && <p role="status">Không có sản phẩm phù hợp. Hãy thử tên hoặc mã SKU khác.</p>}
              <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '14px' }}>
                {products.length === 0 ? (
                  <p style={{ color: 'var(--muted)' }}>Chưa có sản phẩm nào. Hãy tạo bản nháp đầu tiên ở trên.</p>
                ) : (
                  visibleProducts.map((p: ManagedProduct) => {
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

                {selectedProduct.publicationStatus === 'PUBLISHED' || selectedProduct.publicationStatus === 'SUSPENDED' ? (
                  <><div style={{ padding: '14px 18px', borderRadius: '12px', backgroundColor: selectedProduct.publicationStatus === 'SUSPENDED' ? '#fff7ed' : '#ecfdf5', color: selectedProduct.publicationStatus === 'SUSPENDED' ? '#9a3412' : '#065f46', border: `1px solid ${selectedProduct.publicationStatus === 'SUSPENDED' ? '#fed7aa' : '#a7f3d0'}` }}>
                    {selectedProduct.publicationStatus === 'SUSPENDED' ? 'Sản phẩm đang tạm ngừng bán. Bạn vẫn có thể soạn revision; Admin duyệt sẽ không tự mở bán.' : '✓ Sản phẩm đã công bố. Mọi thay đổi mới phải đi qua revision và Admin duyệt.'}
                  </div><StaffProductRevisionPanel product={selectedProduct} categories={categories} brands={brands} /></>
                ) : selectedProduct.publicationStatus === 'DRAFT' ? (
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
                      <label htmlFor="edit-brand">Thương hiệu</label>
                      <select id="edit-brand" name="brandId" defaultValue={selectedProduct.brandId ?? ''} disabled={submitting}>
                        <option value="">Chưa chọn thương hiệu</option>
                        {selectedProduct.brandId && !brands.some(brand => brand.id === selectedProduct.brandId) && <option value={selectedProduct.brandId}>Thương hiệu hiện tại (không còn khả dụng)</option>}
                        {brands.filter(brand => brand.isActive || brand.id === selectedProduct.brandId).map(brand => <option key={brand.id} value={brand.id}>{brand.name}{brand.isActive ? '' : ' (ngừng dùng)'}</option>)}
                      </select>
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
                    <div className="form-field">
                      <label htmlFor="edit-category">Danh mục</label>
                      <select id="edit-category" name="categoryId" defaultValue={selectedProduct.categoryId ?? ''} disabled={submitting}>
                        <option value="">Chưa phân loại</option>
                        {selectedProduct.categoryId && !categories.some(category => category.id === selectedProduct.categoryId) && <option value={selectedProduct.categoryId}>Danh mục hiện tại (không còn khả dụng)</option>}
                        {categories.filter(category => category.isVisible || category.id === selectedProduct.categoryId).map(category => <option key={category.id} value={category.id}>{category.name}{category.isVisible ? '' : ' (đang ẩn)'}</option>)}
                      </select>
                    </div>
                    <button type="submit" className="button-secondary" disabled={submitting}>
                      {submitting ? 'Đang lưu...' : 'Lưu thông tin nháp'}
                    </button>
                  </form>
                ) : (
                  <div className="cart-warning" role="status">Sản phẩm đã lưu trữ. Nội dung, SKU và media chỉ còn ở chế độ xem lịch sử.</div>
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

                <div style={{ borderBottom: '1px solid var(--line)', paddingBottom: '24px' }}>
                  <h3>Thông số kỹ thuật ({selectedProduct.specifications.length})</h3>
                  <div className="table-responsive"><table className="admin-table"><thead><tr><th>Thông số</th><th>Giá trị</th><th>Thứ tự</th><th></th></tr></thead><tbody>
                    {selectedProduct.specifications.map(spec => <tr key={spec.id}><td><strong>{spec.displayName}</strong><br/><small>{spec.key}</small></td><td>{spec.value}</td><td>{spec.displayOrder}</td><td>{selectedProduct.publicationStatus === 'DRAFT' && <button type="button" className="text-button danger" disabled={submitting} onClick={() => void handleDeleteSpecification(spec.key)}>Xóa</button>}</td></tr>)}
                    {selectedProduct.specifications.length === 0 && <tr><td colSpan={4}>Chưa có thông số kỹ thuật.</td></tr>}
                  </tbody></table></div>
                  {selectedProduct.publicationStatus === 'DRAFT' && <form onSubmit={handleUpsertSpecification} style={{ marginTop: '20px', padding: '16px', borderRadius: '14px', backgroundColor: 'var(--paper-deep)' }}>
                    <h4 style={{ margin: '0 0 12px' }}>Thêm hoặc cập nhật thông số</h4><div className="form-row">
                      <label className="form-field">Khóa kỹ thuật<input required pattern="[a-z0-9]+(?:_[a-z0-9]+)*" value={specKey} onChange={e => setSpecKey(e.target.value.toLowerCase())} placeholder="trong_luong" /></label>
                      <label className="form-field">Tên hiển thị<input required value={specName} onChange={e => setSpecName(e.target.value)} placeholder="Trọng lượng" /></label>
                      <label className="form-field">Giá trị<input required value={specValue} onChange={e => setSpecValue(e.target.value)} placeholder="4U (80–84g)" /></label>
                      <label className="form-field">Thứ tự<input type="number" min="0" value={specOrder} onChange={e => setSpecOrder(e.target.value)} /></label>
                    </div><button type="submit" className="button-secondary" disabled={submitting}>Lưu thông số</button>
                  </form>}
                </div>

                {/* Media Section */}
                <div>
                  <h3>Hình ảnh sản phẩm Media ({selectedProduct.images.length})</h3>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '16px', marginTop: '16px' }}>
                    {selectedProduct.images.map((img, index) => (
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
                        <div style={{ padding: '10px', fontSize: '11px', flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: '8px' }}>
                          <div>
                            <strong style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {img.fileKey}
                            </strong>
                            <span style={{ color: 'var(--muted)' }}>Thứ tự: {img.displayOrder}</span>
                          </div>
                          {selectedProduct.publicationStatus === 'DRAFT' && <>
                            <form onSubmit={event => { event.preventDefault(); void handleUpdateImage(img.id, event.currentTarget) }} className="media-metadata-form">
                              <label>Alt text<input name="altText" defaultValue={img.altText ?? ''} maxLength={240} aria-label={`Alt text ảnh ${index + 1}`} /></label>
                              <label>Thứ tự<input name="displayOrder" type="number" min="0" max="10000" defaultValue={img.displayOrder} aria-label={`Thứ tự ảnh ${index + 1}`} /></label>
                              <button type="submit" className="button-secondary" disabled={submitting}>Lưu metadata</button>
                            </form>
                            <div className="media-card-actions">
                              <button type="button" className="button-secondary" aria-label={`Đưa ảnh ${index + 1} lên trước`} disabled={submitting || index === 0} onClick={() => void handleMoveImage(img.id, -1)}>←</button>
                              <button type="button" className="button-secondary" aria-label={`Đưa ảnh ${index + 1} xuống sau`} disabled={submitting || index === selectedProduct.images.length - 1} onClick={() => void handleMoveImage(img.id, 1)}>→</button>
                              <button type="button" className="text-button danger" onClick={() => setDeletingImage({ id: img.id, fileKey: img.fileKey })} disabled={submitting}>Xóa ảnh</button>
                            </div>
                          </>}
                        </div>
                      </div>
                    ))}
                  </div>

                  {selectedProduct.publicationStatus === 'DRAFT' && (
                    <form onSubmit={handleAddImage} className="media-upload-form">
                      <h4>Upload ảnh sản phẩm</h4>
                      <label
                        className={`media-drop-zone${draggingMedia ? ' dragging' : ''}`}
                        onDragEnter={event => { event.preventDefault(); setDraggingMedia(true) }}
                        onDragOver={event => event.preventDefault()}
                        onDragLeave={() => setDraggingMedia(false)}
                        onDrop={event => { event.preventDefault(); setDraggingMedia(false); chooseUploadFiles(Array.from(event.dataTransfer.files)) }}
                      >
                        <span><strong>Kéo ảnh vào đây</strong> hoặc chọn từ máy</span>
                        <small>JPEG, PNG, WebP · tối đa 8 MiB/ảnh · có thể chọn nhiều ảnh</small>
                        <input id="new-img-files" type="file" accept="image/jpeg,image/png,image/webp" multiple onChange={event => chooseUploadFiles(Array.from(event.target.files ?? []))} disabled={submitting} />
                      </label>
                      {uploadFiles.length > 0 && <ul className="media-file-queue" aria-live="polite">{uploadFiles.map(file => <li key={`${file.name}-${file.lastModified}`}><span>{file.name}</span><small>{(file.size / 1024 / 1024).toFixed(2)} MiB</small></li>)}</ul>}
                      <div className="form-row">
                        <div className="form-field">
                          <label htmlFor="new-img-alt">Alt text chung</label>
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
                      <button type="submit" className="button-primary" disabled={submitting || uploadFiles.length === 0}>
                        {submitting ? 'Đang upload...' : `Upload ${uploadFiles.length || ''} ảnh`}
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
          <details className="management-card inventory-action" id="stock-in-form">
            <summary>Nhập hàng vào kho</summary>
            <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 16px' }}>
              Chọn phiên bản sản phẩm và số lượng thực nhận. Tồn khả dụng sẽ tăng sau khi xác nhận.
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
                  Giữ nguyên mã phiếu khi thử lại để tránh ghi nhận cùng một lần nhập hàng hai lần.
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
          </details>

          <details className="management-card inventory-action">
            <summary>Đề nghị giảm tồn kho</summary>
            <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 16px' }}>
              Gửi số lượng và lý do để quản trị viên duyệt. Hàng đang giữ cho đơn mua không bị ảnh hưởng.
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
          </details>

          {/* Inventory Table Card */}
          <div className="management-card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '12px' }}>
              <div>
                <h2 style={{ margin: 0 }}>Bảng theo dõi Tồn kho ({inventoryList.length} SKU)</h2>
                <label className="workspace-search">Tìm trong danh sách tồn kho<input type="search" value={inventorySearch} onChange={event => setInventorySearch(event.target.value)} placeholder="Tên sản phẩm, phiên bản hoặc mã SKU" /></label>
                {!loadingInventory && inventoryList.length > 0 && visibleInventory.length === 0 && <p role="status">Không tìm thấy SKU phù hợp. Hãy thử từ khóa khác.</p>}
                <p style={{ color: 'var(--muted)', fontSize: '13px', margin: '4px 0 0' }}>
                  Đẳng thức phân vùng: <strong>Vật lý = Khả dụng + Đang giữ + Đã phân bổ + Cách ly + Hư hỏng</strong>
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
                    <th style={{ textAlign: 'right' }}>Cách ly</th>
                    <th style={{ textAlign: 'right' }}>Hư hỏng</th>
                    <th>Trạng thái</th>
                    <th style={{ textAlign: 'center' }}>Thao tác</th>
                  </tr>
                </thead>
                <tbody>
                  {inventoryList.length === 0 ? (
                    <tr>
                      <td colSpan={10} style={{ textAlign: 'center', padding: '30px', color: 'var(--muted)' }}>
                        {loadingInventory ? 'Đang tải dữ liệu kho...' : 'Chưa có biến thể SKU nào trong hệ thống.'}
                      </td>
                    </tr>
                  ) : (
                    visibleInventory.map(item => (
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
                        <td style={{ textAlign: 'right', color: '#9a3412', fontWeight: 700 }}>{item.quarantinedQty}</td>
                        <td style={{ textAlign: 'right', color: '#b91c1c', fontWeight: 700 }}>{item.damagedQty}</td>
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
                                const form = document.getElementById('stock-in-form') as HTMLDetailsElement | null
                                if (form) form.open = true
                                document.getElementById('stock-qty-input')?.focus()
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
        <div style={{ display: 'grid', gap: '24px' }}>
          <CodOrdersPanel onConfirmed={() => setFulfillmentRefreshKey(k => k + 1)} />
          <FulfillmentPanel refreshKey={fulfillmentRefreshKey} />
        </div>
      ) : activeTab === 'carriers' ? (
        <StaffCarrierTrackingPanel />
      ) : activeTab === 'reviews' ? (
        <ReviewModerationPanel />
      ) : activeTab === 'returns' ? (
        <div style={{ display: 'grid', gap: 24 }}>
          <StaffReturnsPanel />
          <ExchangeFulfillmentPanel />
        </div>
      ) : <StaffTaskSummaryPanel />}
    </section>
  )
}
