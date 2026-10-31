import { useCallback, useState } from 'react'
import { useLoaderData, useRevalidator } from 'react-router-dom'
import {
  type ManagedProduct,
  adminLoader,
  publishProduct,
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

export function AdminDashboardPage() {
  const { products } = useLoaderData<typeof adminLoader>()
  const revalidator = useRevalidator()

  const [activeTab, setActiveTab] = useState<'products' | 'adjustments' | 'reports' | 'flashSales'>('products')
  const [selectedProductId, setSelectedProductId] = useState<string | null>(products[0]?.id ?? null)
  const [submitting, setSubmitting] = useState(false)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  // Map to hold temporary price edits: { [skuId]: priceString }
  const [priceInputs, setPriceInputs] = useState<Record<string, string>>({})
  const [adjustments, setAdjustments] = useState<InventoryAdjustment[]>([])
  const [loadingAdjustments, setLoadingAdjustments] = useState(false)

  const selectedProduct = products.find(p => p.id === selectedProductId) ?? null

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
      <p className="eyebrow">Back Office · Admin</p>
      <h1>{activeTab === 'products' ? 'Kiểm soát Giá & Phê duyệt Công bố' : activeTab === 'adjustments' ? 'Phê duyệt Điều chỉnh Tồn kho' : activeTab === 'flashSales' ? 'Quản lý Flash Sale' : 'Báo cáo & Audit Log'}</h1>
      <p className="page-intro">
        {activeTab === 'products'
          ? 'Admin chịu trách nhiệm kiểm duyệt biến thể, định mức giá bán và công bố sản phẩm lên Storefront. Mọi thay đổi đều được ghi Audit Log.'
          : activeTab === 'adjustments' ? 'Admin duyệt hoặc từ chối đề nghị giảm tồn của Staff. Chỉ tồn khả dụng được giảm; Reserved và Allocated không bị tác động.'
            : activeTab === 'flashSales' ? 'Tạo chiến dịch, chọn sản phẩm và theo dõi ưu đãi theo từng khung giờ.' : 'Theo dõi doanh thu, cảnh báo tồn kho và lịch sử thao tác quản trị.'}
      </p>

      <div className="dashboard-tabs" role="tablist" aria-label="Khu vực quản trị Admin">
        <button type="button" role="tab" aria-selected={activeTab === 'products'} className={activeTab === 'products' ? 'active' : ''} onClick={() => setActiveTab('products')}>
          01 · Giá & Publish
        </button>
        <button type="button" role="tab" aria-selected={activeTab === 'adjustments'} className={activeTab === 'adjustments' ? 'active' : ''} onClick={() => {
          setActiveTab('adjustments')
          void refreshAdjustments()
        }}>
          02 · Duyệt giảm tồn
        </button>
        <button type="button" role="tab" aria-selected={activeTab === 'reports'} className={activeTab === 'reports' ? 'active' : ''} onClick={() => setActiveTab('reports')}>
          03 · Báo cáo & Audit
        </button>
        <button type="button" role="tab" aria-selected={activeTab === 'flashSales'} className={activeTab === 'flashSales' ? 'active' : ''} onClick={() => setActiveTab('flashSales')}>
          04 · Flash Sale
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
        {/* Left Column: List of Products */}
        <div className="management-card">
          <h2>Danh sách sản phẩm ({products.length})</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '16px' }}>
            {products.length === 0 ? (
              <p style={{ color: 'var(--muted)' }}>Chưa có sản phẩm nào trong hệ thống.</p>
            ) : (
              products.map((p: ManagedProduct) => {
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

              {/* Publish Section */}
              <div style={{ backgroundColor: 'var(--paper-deep)', padding: '20px', borderRadius: '14px' }}>
                <h3>Kiểm duyệt & Công bố sản phẩm</h3>
                {selectedProduct.publicationStatus === 'PUBLISHED' ? (
                  <p style={{ color: '#065f46', margin: 0, fontWeight: 600 }}>
                    ✓ Sản phẩm này đang hoạt động trên Catalog công khai của HighCommerce.
                  </p>
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
                            onClick={() => handlePublish(selectedProduct.id)}
                            disabled={submitting || !canPublish}
                            style={{ alignSelf: 'flex-start' }}
                          >
                            {submitting ? 'Đang công bố...' : '🚀 Duyệt & Công bố lên Storefront (PUBLISH)'}
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
                          <button type="button" className="button-primary" disabled={submitting} onClick={() => void handleAdjustmentDecision(item.id, 'APPROVE')} style={{ minHeight: 34, padding: '0 12px', fontSize: 12 }}>
                            Duyệt
                          </button>
                          <button type="button" className="button-secondary" disabled={submitting} onClick={() => void handleAdjustmentDecision(item.id, 'REJECT')} style={{ minHeight: 34, padding: '0 12px', fontSize: 12 }}>
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
      ) : activeTab === 'reports' ? (
        <AdminReportingPanel />
      ) : (
        <AdminFlashSalePanel products={products} />
      )}
    </section>
  )
}
