import { useCallback, useEffect, useState } from 'react'
import { useLoaderData, useRevalidator } from 'react-router-dom'
import {
  type ManagedProduct,
  adminLoader,
  archiveProduct,
  publishProduct,
  resumeProduct,
  suspendProduct,
  updateSkuPrice,
} from '../features/product-management/product-management.api'
import { formatPrice } from '../features/catalog/catalog.api'
import {
  type InventoryAdjustment,
  decideAdjustmentRequest,
  getAdjustmentRequests,
} from '../features/inventory/inventory.api'
import { AdminReportingPanel } from '../features/reporting/AdminReportingPanel'
import { AdminFlashSalePanel } from '../features/flash-sale/AdminFlashSalePanel'
import { AdminAccountsPanel } from '../features/admin-accounts/AdminAccountsPanel'
import { ReviewModerationPanel } from '../features/reviews/ReviewModerationPanel'
import { AdminVouchersPanel } from '../features/vouchers/AdminVouchersPanel'
import { AdminReturnsPanel } from '../features/returns/AdminReturnsPanel'
import { AdminCategoriesPanel } from '../features/categories/AdminCategoriesPanel'
import { AdminBrandsPanel } from '../features/brands/AdminBrandsPanel'
import { useWorkspaceSection } from '../components/layout/useWorkspaceSection'
import { ConfirmDialog } from '../components/layout/ConfirmDialog'
import { AdminProductRevisionsPanel } from '../features/product-revisions/AdminProductRevisionsPanel'
import { AdminShippingPanel } from '../features/shipping/AdminShippingPanel'
import { AdminCarriersPanel } from '../features/carrier-tracking/AdminCarriersPanel'
import { AdminPayOsReconciliationPanel } from '../features/payments/AdminPayOsReconciliationPanel'

export function AdminDashboardPage() {
  const { products } = useLoaderData<typeof adminLoader>()
  const revalidator = useRevalidator()

  const [activeTab, setActiveTab] = useWorkspaceSection(['products', 'revisions', 'categories', 'brands', 'adjustments', 'reports', 'flashSales', 'accounts', 'reviews', 'vouchers', 'shipping', 'carriers', 'returns', 'payos'] as const, 'products')
  const [selectedProductId, setSelectedProductId] = useState<string | null>(products[0]?.id ?? null)
  const [submitting, setSubmitting] = useState(false)
  const [confirmation, setConfirmation] = useState<{ title: string; description: string; action: () => Promise<void> } | null>(null)
  const [productSearch, setProductSearch] = useState('')
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [lifecycleReason, setLifecycleReason] = useState('')

  // Map to hold temporary price edits: { [skuId]: priceString }
  const [priceInputs, setPriceInputs] = useState<Record<string, string>>({})
  const [adjustments, setAdjustments] = useState<InventoryAdjustment[]>([])
  const [loadingAdjustments, setLoadingAdjustments] = useState(false)

  const selectedProduct = products.find(p => p.id === selectedProductId) ?? null
  const visibleProducts = products.filter(p => `${p.title} ${p.skus.map(sku => sku.skuCode).join(' ')}`.toLocaleLowerCase('vi').includes(productSearch.trim().toLocaleLowerCase('vi')))

  const refreshAdjustments = useCallback(async () => {
    setLoadingAdjustments(true)
    try {
      setAdjustments(await getAdjustmentRequests())
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không tải được danh sách đề nghị điều chỉnh.' })
    } finally {
      setLoadingAdjustments(false)
    }
  }, [])

  const handlePriceChange = (skuId: string, val: string) => {
    setPriceInputs(prev => ({ ...prev, [skuId]: val }))
  }

  useEffect(() => {
    if (activeTab !== 'adjustments') return
    let ignore = false
    getAdjustmentRequests().then(data => {
      if (!ignore) setAdjustments(data)
    }).catch(() => {
      if (!ignore) setMessage({ type: 'error', text: 'Không tải được đề nghị điều chỉnh. Vui lòng thử lại.' })
    })
    return () => { ignore = true }
  }, [activeTab])

  const handleSavePrice = async (skuId: string) => {
    const rawVal = priceInputs[skuId]
    const priceNum = parseFloat(rawVal ?? '')

    if (isNaN(priceNum) || priceNum <= 0) {
      setMessage({ type: 'error', text: 'Giá bán phải là một số lớn hơn 0.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      await updateSkuPrice(skuId, priceNum)
      setMessage({ type: 'success', text: `Cập nhật giá thành công: ${formatPrice(priceNum)}. Đã lưu vào Audit Log.` })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Cập nhật giá SKU thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handlePublish = async (productId: string) => {
    setSubmitting(true)
    setMessage(null)
    try {
      const res = await publishProduct(productId)
      setMessage({
        type: 'success',
        text: `Đã công bố sản phẩm thành công lên Catalog công khai lúc ${new Date(res.publishedAt).toLocaleTimeString('vi-VN')}. Đã lưu Audit Log.`,
      })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Công bố sản phẩm thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleLifecycle = async (action: 'suspend' | 'resume' | 'archive') => {
    if (!selectedProduct) return
    if (action !== 'resume' && lifecycleReason.trim().length < 5) {
      setMessage({ type: 'error', text: 'Vui lòng nhập lý do ít nhất 5 ký tự.' })
      return
    }
    setSubmitting(true)
    setMessage(null)
    try {
      const result = action === 'suspend'
        ? await suspendProduct(selectedProduct.id, lifecycleReason.trim())
        : action === 'archive'
          ? await archiveProduct(selectedProduct.id, lifecycleReason.trim())
          : await resumeProduct(selectedProduct.id)
      setLifecycleReason('')
      setMessage({ type: 'success', text: `Đã chuyển sản phẩm sang ${result.publicationStatus}. Thay đổi đã được ghi Audit Log.` })
      revalidator.revalidate()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể thay đổi trạng thái sản phẩm.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleAdjustmentDecision = async (id: string, decision: 'APPROVE' | 'REJECT') => {
    setSubmitting(true)
    setMessage(null)
    try {
      const result = await decideAdjustmentRequest(id, decision)
      const text = result.request.reviewStatus === 'APPLIED'
        ? `Đã duyệt và áp dụng giảm ${Math.abs(result.request.deltaQty)} sản phẩm cho SKU ${result.request.skuCode}.`
        : `Đã từ chối đề nghị giảm tồn cho SKU ${result.request.skuCode}.`
      setMessage({ type: 'success', text })
      await refreshAdjustments()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Không thể xử lý đề nghị điều chỉnh.' })
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section className="content-page workspace-page">
      {confirmation && <ConfirmDialog title={confirmation.title} description={confirmation.description} confirmLabel="Xác nhận" busy={submitting} onCancel={() => setConfirmation(null)} onConfirm={() => { void confirmation.action().finally(() => setConfirmation(null)) }} />}
      <p className="eyebrow">Back Office · Admin</p>
      <h1>
        {activeTab === 'products'
          ? 'Kiểm soát Giá & Phê duyệt Công bố'
          : activeTab === 'revisions'
            ? 'Duyệt thay đổi Sản phẩm'
          : activeTab === 'categories'
            ? 'Quản trị Danh mục Sản phẩm'
          : activeTab === 'brands'
            ? 'Quản trị Thương hiệu'
          : activeTab === 'adjustments'
            ? 'Phê duyệt Điều chỉnh Tồn kho'
            : activeTab === 'flashSales'
              ? 'Quản lý Flash Sale'
              : activeTab === 'accounts'
                ? 'Quản trị Tài khoản & Nhân sự'
                : activeTab === 'reviews'
                  ? 'Kiểm duyệt Đánh giá Sản phẩm'
                  : activeTab === 'vouchers'
                    ? 'Quản lý Mã giảm giá & Ngân sách'
                    : activeTab === 'shipping'
                      ? 'Quản lý Vận chuyển & Phí'
                      : activeTab === 'carriers'
                        ? 'Hãng vận chuyển & Tracking'
                      : activeTab === 'payos'
                        ? 'Đối soát & Hoàn tiền PayOS'
                      : activeTab === 'returns'
                        ? 'Đổi trả & Hoàn tiền COD'
                        : 'Báo cáo & Audit Log'}
      </h1>
      <p className="page-intro">
        {activeTab === 'products'
          ? 'Admin chịu trách nhiệm kiểm duyệt biến thể, định mức giá bán và công bố sản phẩm lên Storefront. Mọi thay đổi đều được ghi Audit Log.'
          : activeTab === 'revisions'
            ? 'So sánh snapshot Staff đề xuất với nội dung đang bán và chỉ cập nhật Storefront sau khi duyệt.'
          : activeTab === 'categories'
            ? 'Tạo và chuẩn hóa taxonomy dùng chung cho sản phẩm, bộ lọc và điều hướng Storefront.'
          : activeTab === 'brands'
            ? 'Quản lý hãng sản xuất dùng chung cho dữ liệu sản phẩm, bộ lọc và trang chi tiết.'
          : activeTab === 'adjustments'
            ? 'Admin duyệt hoặc từ chối đề nghị giảm tồn của Staff. Chỉ tồn khả dụng được giảm; Reserved và Allocated không bị tác động.'
            : activeTab === 'flashSales'
              ? 'Tạo chiến dịch, chọn sản phẩm và theo dõi ưu đãi theo từng khung giờ.'
              : activeTab === 'accounts'
                ? 'Quản lý danh sách tài khoản khách hàng, mời nhân sự Staff, khóa/mở tài khoản và thu hồi phiên đăng nhập.'
                : activeTab === 'reviews'
                  ? 'Admin kiểm duyệt nhận xét và đánh giá của khách hàng, ẩn các nội dung vi phạm hoặc khôi phục hiển thị.'
                  : activeTab === 'vouchers'
                    ? 'Thiết lập mã ưu đãi, giới hạn lượt dùng và theo dõi tiến độ tiêu hao ngân sách.'
                    : activeTab === 'shipping'
                      ? 'Cấu hình phí cơ bản, ETA, ngưỡng miễn phí và phụ phí tỉnh/thành cho các báo giá mới.'
                    : activeTab === 'carriers'
                      ? 'Quản lý registry hãng, mẫu URL tracking và trạng thái nhận vận đơn mới.'
                    : activeTab === 'payos'
                      ? 'Tiếp nhận case tiền đến muộn hoặc sai lệch, ghi kết luận và chứng từ hoàn tiền thủ công với Audit Log đầy đủ.'
                    : activeTab === 'returns'
                      ? 'Phê duyệt hoặc từ chối yêu cầu đổi trả của khách hàng, ghi mã chứng từ hoàn tiền thủ công cho đơn COD.'
                      : 'Theo dõi doanh thu, cảnh báo tồn kho và lịch sử thao tác quản trị.'}
      </p>

      <div className="dashboard-tabs" role="tablist" aria-label="Khu vực quản trị Admin" onKeyDown={event => {
        const keys = ['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp', 'Home', 'End']
        if (!keys.includes(event.key)) return
        event.preventDefault()
        const tabs = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]'))
        const current = tabs.indexOf(document.activeElement as HTMLButtonElement)
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
          : (current + (event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 1) + tabs.length) % tabs.length
        tabs[next]?.focus()
        tabs[next]?.click()
      }}>
        <button id="admin-tab-products" aria-controls="admin-section" tabIndex={activeTab === 'products' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'products'} className={activeTab === 'products' ? 'active' : ''} onClick={() => setActiveTab('products')}>
          Giá & Publish
        </button>
        <button id="admin-tab-revisions" aria-controls="admin-section" tabIndex={activeTab === 'revisions' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'revisions'} className={activeTab === 'revisions' ? 'active' : ''} onClick={() => setActiveTab('revisions')}>
          Duyệt nội dung
        </button>
        <button id="admin-tab-categories" aria-controls="admin-section" tabIndex={activeTab === 'categories' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'categories'} className={activeTab === 'categories' ? 'active' : ''} onClick={() => setActiveTab('categories')}>
          Danh mục
        </button>
        <button id="admin-tab-brands" aria-controls="admin-section" tabIndex={activeTab === 'brands' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'brands'} className={activeTab === 'brands' ? 'active' : ''} onClick={() => setActiveTab('brands')}>
          Thương hiệu
        </button>
        <button id="admin-tab-adjustments" aria-controls="admin-section" tabIndex={activeTab === 'adjustments' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'adjustments'} className={activeTab === 'adjustments' ? 'active' : ''} onClick={() => {
          setActiveTab('adjustments')
        }}>
          Duyệt giảm tồn
        </button>
        <button id="admin-tab-reports" aria-controls="admin-section" tabIndex={activeTab === 'reports' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'reports'} className={activeTab === 'reports' ? 'active' : ''} onClick={() => setActiveTab('reports')}>
          Báo cáo & Audit
        </button>
        <button id="admin-tab-flashSales" aria-controls="admin-section" tabIndex={activeTab === 'flashSales' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'flashSales'} className={activeTab === 'flashSales' ? 'active' : ''} onClick={() => setActiveTab('flashSales')}>
          Flash Sale
        </button>
        <button id="admin-tab-accounts" aria-controls="admin-section" tabIndex={activeTab === 'accounts' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'accounts'} className={activeTab === 'accounts' ? 'active' : ''} onClick={() => setActiveTab('accounts')}>
          Tài khoản &amp; Nhân sự
        </button>
        <button id="admin-tab-reviews" aria-controls="admin-section" tabIndex={activeTab === 'reviews' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'reviews'} className={activeTab === 'reviews' ? 'active' : ''} onClick={() => setActiveTab('reviews')}>
          Kiểm duyệt Đánh giá
        </button>
        <button id="admin-tab-vouchers" aria-controls="admin-section" tabIndex={activeTab === 'vouchers' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'vouchers'} className={activeTab === 'vouchers' ? 'active' : ''} onClick={() => setActiveTab('vouchers')}>
          Mã giảm giá
        </button>
        <button id="admin-tab-shipping" aria-controls="admin-section" tabIndex={activeTab === 'shipping' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'shipping'} className={activeTab === 'shipping' ? 'active' : ''} onClick={() => setActiveTab('shipping')}>
          Vận chuyển &amp; Phí
        </button>
        <button id="admin-tab-carriers" aria-controls="admin-section" tabIndex={activeTab === 'carriers' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'carriers'} className={activeTab === 'carriers' ? 'active' : ''} onClick={() => setActiveTab('carriers')}>
          Hãng &amp; Tracking
        </button>
        <button id="admin-tab-returns" aria-controls="admin-section" tabIndex={activeTab === 'returns' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'returns'} className={activeTab === 'returns' ? 'active' : ''} onClick={() => setActiveTab('returns')}>
          Đổi trả &amp; Hoàn tiền
        </button>
        <button id="admin-tab-payos" aria-controls="admin-section" tabIndex={activeTab === 'payos' ? 0 : -1} type="button" role="tab" aria-selected={activeTab === 'payos'} className={activeTab === 'payos' ? 'active' : ''} onClick={() => setActiveTab('payos')}>
          Đối soát PayOS
        </button>
      </div>

      {message && (
        <div
          role={message.type === 'success' ? 'status' : 'alert'}
          className={`alert ${message.type === 'success' ? 'success' : 'error'}`}
          style={{ margin: '20px 0' }}
        >
          {message.text}
        </div>
      )}

      <div id="admin-section" role="tabpanel" aria-labelledby={`admin-tab-${activeTab}`} tabIndex={0}>
      {activeTab === 'products' ? (
      <div className="management-layout">
        {/* Left Column: List of Products */}
        <div className="management-card">
          <h2>Danh sách sản phẩm ({products.length})</h2>
          <label className="workspace-search">Tìm sản phẩm<input type="search" value={productSearch} onChange={event => setProductSearch(event.target.value)} placeholder="Tên sản phẩm hoặc mã SKU" /></label>
          {products.length > 0 && visibleProducts.length === 0 && <p role="status">Không có sản phẩm phù hợp. Hãy thử tên hoặc mã SKU khác.</p>}
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '16px' }}>
            {products.length === 0 ? (
              <p style={{ color: 'var(--muted)' }}>Chưa có sản phẩm nào trong hệ thống.</p>
            ) : (
              visibleProducts.map((p: ManagedProduct) => {
                const isSelected = p.id === selectedProductId
                const hasZeroPriceSku = p.skus.some(s => s.skuStatus === 'ACTIVE' && s.regularPrice <= 0)
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
                      padding: '14px',
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
                    <div style={{ display: 'flex', justifyContent: 'space-between', width: '100%', fontSize: '12px', color: 'var(--muted)' }}>
                      <span>{p.skus.length} SKU · {p.images.length} ảnh</span>
                      {p.publicationStatus === 'DRAFT' && hasZeroPriceSku && (
                        <span style={{ color: '#d97706', fontWeight: 600 }}>⚠️ Chưa định giá đủ</span>
                      )}
                    </div>
                  </button>
                )
              })
            )}
          </div>
        </div>

        {/* Right Column: Selected Product Pricing & Publish */}
        <div>
          {selectedProduct ? (
            <div className="management-card" style={{ display: 'flex', flexDirection: 'column', gap: '28px' }}>
              <div style={{ borderBottom: '1px solid var(--line)', paddingBottom: '20px' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '12px' }}>
                  <h2 style={{ margin: 0 }}>{selectedProduct.title}</h2>
                  <span className={`badge badge-${selectedProduct.publicationStatus.toLowerCase()}`}>
                    {selectedProduct.publicationStatus}
                  </span>
                </div>
                <p style={{ color: 'var(--muted)', fontSize: '13px', marginTop: '6px' }}>
                  Mô tả: {selectedProduct.description || 'Chưa có mô tả.'}
                </p>
                {selectedProduct.publishedAt && (
                  <p style={{ color: '#065f46', fontSize: '13px', fontWeight: 600 }}>
                    Đã công bố vào: {new Date(selectedProduct.publishedAt).toLocaleString('vi-VN')}
                  </p>
                )}
              </div>

              {/* Pricing Section */}
              <div>
                <h3>Định giá các biến thể SKU</h3>
                <p style={{ fontSize: '13px', color: 'var(--muted)', marginTop: '4px' }}>
                  Nhập số tiền VNĐ cho từng biến thể SKU. Thao tác cập nhật giá sẽ tự động ghi vết thay đổi vào Audit Log.
                </p>

                <div className="table-responsive">
                  <table className="admin-table">
                    <thead>
                      <tr>
                        <th>Mã SKU</th>
                        <th>Tùy chọn</th>
                        <th>Giá hiện tại</th>
                        <th>Cập nhật giá mới (VNĐ)</th>
                        <th>Thao tác</th>
                      </tr>
                    </thead>
                    <tbody>
                      {selectedProduct.skus.length === 0 ? (
                        <tr>
                          <td colSpan={5} style={{ textAlign: 'center', color: 'var(--muted)', padding: '24px' }}>
                            Sản phẩm này chưa có biến thể SKU nào. Nhân viên (Staff) cần tạo SKU trước khi Admin có thể định giá.
                          </td>
                        </tr>
                      ) : (
                        selectedProduct.skus.map(s => {
                          const currentVal = priceInputs[s.id] ?? (s.regularPrice > 0 ? s.regularPrice.toString() : '')
                          return (
                            <tr key={s.id}>
                              <td><strong>{s.skuCode}</strong></td>
                              <td>{s.optionLabel}</td>
                              <td>{s.regularPrice > 0 ? formatPrice(s.regularPrice) : <span style={{ color: '#dc2626' }}>Chưa có giá</span>}</td>
                              <td>
                                <input
                                  type="number"
                                  aria-label={`Giá mới cho SKU ${s.skuCode}`}
                                  min="1"
                                  step="1000"
                                  placeholder="VD: 3200000"
                                  value={currentVal}
                                  onChange={e => handlePriceChange(s.id, e.target.value)}
                                  disabled={submitting}
                                  style={{
                                    padding: '6px 10px',
                                    borderRadius: '8px',
                                    border: '1px solid var(--line)',
                                    width: '140px',
                                  }}
                                />
                              </td>
                              <td>
                                <button
                                  type="button"
                                  className="button-secondary"
                                  onClick={() => handleSavePrice(s.id)}
                                  disabled={submitting || !priceInputs[s.id]}
                                  style={{ padding: '6px 12px', minHeight: 'unset', fontSize: '13px' }}
                                >
                                  Lưu giá
                                </button>
                              </td>
                            </tr>
                          )
                        })
                      )}
                    </tbody>
                  </table>
                </div>
              </div>

              {/* Publication lifecycle */}
              <div style={{ backgroundColor: 'var(--paper-deep)', padding: '20px', borderRadius: '14px' }}>
                <h3>Vòng đời kinh doanh sản phẩm</h3>
                {selectedProduct.publicationStatus === 'PUBLISHED' ? (
                  <div className="lifecycle-actions">
                    <p style={{ color: '#065f46', margin: 0, fontWeight: 600 }}>✓ Sản phẩm này đang hoạt động trên Catalog công khai.</p>
                    <label htmlFor="lifecycle-reason">Lý do tạm ngừng</label>
                    <textarea id="lifecycle-reason" value={lifecycleReason} onChange={event => setLifecycleReason(event.target.value)} maxLength={500} rows={3} placeholder="Ví dụ: cần kiểm tra lại thông tin hoặc chất lượng lô hàng" disabled={submitting} />
                    <button type="button" className="button-secondary danger" disabled={submitting || lifecycleReason.trim().length < 5} onClick={() => setConfirmation({ title: 'Tạm ngừng bán sản phẩm?', description: 'Sản phẩm sẽ biến mất khỏi Catalog và Checkout mới bị chặn. Dữ liệu lịch sử vẫn được giữ.', action: () => handleLifecycle('suspend') })}>Tạm ngừng bán</button>
                  </div>
                ) : selectedProduct.publicationStatus === 'SUSPENDED' ? (
                  <div className="lifecycle-actions">
                    <p className="cart-warning">Sản phẩm đang tạm ngừng bán. Revision nội dung vẫn có thể được duyệt nhưng không tự mở bán.</p>
                    {selectedProduct.statusReason && <p><strong>Lý do:</strong> {selectedProduct.statusReason}</p>}
                    <button type="button" className="button-primary" disabled={submitting} onClick={() => setConfirmation({ title: 'Mở bán lại sản phẩm?', description: 'Sản phẩm và Flash Sale còn hiệu lực có thể xuất hiện lại ngay trên Storefront.', action: () => handleLifecycle('resume') })}>Mở bán lại</button>
                    <label htmlFor="archive-reason">Lý do lưu trữ vĩnh viễn</label>
                    <textarea id="archive-reason" value={lifecycleReason} onChange={event => setLifecycleReason(event.target.value)} maxLength={500} rows={3} placeholder="Ví dụ: sản phẩm đã ngừng kinh doanh" disabled={submitting} />
                    <button type="button" className="button-secondary danger" disabled={submitting || lifecycleReason.trim().length < 5} onClick={() => setConfirmation({ title: 'Lưu trữ sản phẩm?', description: 'ARCHIVED là trạng thái terminal và không thể mở bán lại trong phiên bản hiện tại.', action: () => handleLifecycle('archive') })}>Lưu trữ vĩnh viễn</button>
                  </div>
                ) : selectedProduct.publicationStatus === 'ARCHIVED' ? (
                  <div className="lifecycle-actions"><p className="cart-warning">Sản phẩm đã lưu trữ và không thể mở bán lại.</p>{selectedProduct.statusReason && <p><strong>Lý do:</strong> {selectedProduct.statusReason}</p>}</div>
                ) : (
                  <div>
                    <p style={{ fontSize: '13px', color: 'var(--muted)', margin: '0 0 16px' }}>
                      Điều kiện để công bố: Sản phẩm phải ở trạng thái DRAFT, có ít nhất một SKU ACTIVE và tất cả SKU ACTIVE phải có giá lớn hơn 0.
                    </p>
                    {(() => {
                      const activeSkus = selectedProduct.skus.filter(s => s.skuStatus === 'ACTIVE')
                      const hasActiveSkus = activeSkus.length > 0
                      const allActivePriced = hasActiveSkus && activeSkus.every(s => s.regularPrice > 0)
                      const canPublish = hasActiveSkus && allActivePriced

                      return (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                          {!canPublish && (
                            <div style={{ color: '#b45309', fontSize: '13px' }}>
                              {!hasActiveSkus && <div>• Cần có ít nhất 1 biến thể SKU active.</div>}
                              {!allActivePriced && <div>• Vẫn còn SKU active chưa được định giá (&gt; 0 VNĐ). Hãy lưu giá cho tất cả SKU active trước khi công bố.</div>}
                            </div>
                          )}
                          <button
                            type="button"
                            className="button-primary"
                            onClick={() => setConfirmation({ title: 'Công bố sản phẩm?', description: selectedProduct.title, action: () => handlePublish(selectedProduct.id) })}
                            disabled={submitting || !canPublish}
                            style={{ alignSelf: 'flex-start' }}
                          >
                            {submitting ? 'Đang công bố...' : 'Công bố sản phẩm'}
                          </button>
                        </div>
                      )
                    })()}
                  </div>
                )}
              </div>
            </div>
          ) : (
            <div className="management-card" style={{ textAlign: 'center', padding: '60px 20px', color: 'var(--muted)' }}>
              <h3>Chưa chọn sản phẩm nào</h3>
              <p>Vui lòng chọn một sản phẩm từ danh sách bên trái để xem biến thể và định giá.</p>
            </div>
          )}
        </div>
      </div>
      ) : activeTab === 'revisions' ? (
        <AdminProductRevisionsPanel />
      ) : activeTab === 'adjustments' ? (
        <div className="management-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 16, alignItems: 'center', marginBottom: 18 }}>
            <div>
              <h2 style={{ margin: 0 }}>Đề nghị điều chỉnh ({adjustments.length})</h2>
              <p style={{ color: 'var(--muted)', margin: '6px 0 0', fontSize: 13 }}>
                Khi duyệt, hệ thống kiểm tra lại tồn khả dụng và ghi Movement + Audit trong cùng transaction.
              </p>
            </div>
            <button type="button" className="button-secondary" onClick={() => void refreshAdjustments()} disabled={loadingAdjustments || submitting}>
              {loadingAdjustments ? 'Đang tải...' : '🔄 Làm mới'}
            </button>
          </div>

          <div className="table-responsive">
            <table className="admin-table">
              <thead>
                <tr>
                  <th>SKU / Sản phẩm</th>
                  <th>Người đề nghị</th>
                  <th style={{ textAlign: 'right' }}>Số lượng giảm</th>
                  <th>Lý do</th>
                  <th>Trạng thái</th>
                  <th>Thời gian</th>
                  <th>Quyết định</th>
                </tr>
              </thead>
              <tbody>
                {adjustments.length === 0 ? (
                  <tr><td colSpan={7} style={{ textAlign: 'center', color: 'var(--muted)', padding: 28 }}>Không có đề nghị điều chỉnh nào.</td></tr>
                ) : adjustments.map(item => (
                  <tr key={item.id}>
                    <td><strong>{item.skuCode}</strong><br /><span style={{ color: 'var(--muted)', fontSize: 12 }}>{item.productTitle}</span></td>
                    <td>{item.requestedByName}</td>
                    <td style={{ textAlign: 'right', color: '#b91c1c', fontWeight: 800 }}>{item.deltaQty}</td>
                    <td>{item.reason}</td>
                    <td><span className={`badge badge-${item.reviewStatus.toLowerCase()}`}>{item.reviewStatus}</span></td>
                    <td>{new Date(item.createdAt).toLocaleString('vi-VN')}</td>
                    <td>
                      {item.reviewStatus === 'PENDING' ? (
                        <div style={{ display: 'flex', gap: 8 }}>
                          <button type="button" className="button-primary" disabled={submitting} onClick={() => setConfirmation({ title: 'Duyệt giảm tồn kho?', description: `${item.skuCode}: giảm ${Math.abs(item.deltaQty)}. ${item.reason}`, action: () => handleAdjustmentDecision(item.id, 'APPROVE') })} style={{ minHeight: 34, padding: '0 12px', fontSize: 12 }}>
                            Duyệt
                          </button>
                          <button type="button" className="button-secondary" disabled={submitting} onClick={() => setConfirmation({ title: 'Từ chối đề nghị?', description: `${item.skuCode}: ${item.reason}`, action: () => handleAdjustmentDecision(item.id, 'REJECT') })} style={{ minHeight: 34, padding: '0 12px', fontSize: 12 }}>
                            Từ chối
                          </button>
                        </div>
                      ) : (
                        <span style={{ color: 'var(--muted)', fontSize: 12 }}>{item.reviewedByName ?? 'Đã xử lý'}</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : activeTab === 'categories' ? (
        <AdminCategoriesPanel />
      ) : activeTab === 'brands' ? (
        <AdminBrandsPanel />
      ) : activeTab === 'reports' ? (
        <AdminReportingPanel />
      ) : activeTab === 'flashSales' ? (
        <AdminFlashSalePanel products={products} />
      ) : activeTab === 'accounts' ? (
        <AdminAccountsPanel />
      ) : activeTab === 'vouchers' ? (
        <AdminVouchersPanel />
      ) : activeTab === 'shipping' ? (
        <AdminShippingPanel />
      ) : activeTab === 'carriers' ? (
        <AdminCarriersPanel />
      ) : activeTab === 'returns' ? (
        <AdminReturnsPanel />
      ) : activeTab === 'payos' ? (
        <AdminPayOsReconciliationPanel />
      ) : (
        <ReviewModerationPanel />
      )}
      </div>
    </section>
  )
}
