import { useEffect, useState, useCallback } from 'react'
import type { ManagedProduct } from '../product-management/product-management.api'
import {
  type FlashSaleCampaignSummary,
  type FlashSaleCampaignDetail,
  type FlashSalePerformance,
  getAdminFlashSales,
  getAdminFlashSale,
  createFlashSale,
  addFlashSaleOffer,
  deleteFlashSaleOffer,
  publishFlashSale,
  cancelFlashSale,
  getFlashSalePerformance,
} from './flash-sale.api'
import { formatPrice } from '../catalog/catalog.api'

interface AdminFlashSalePanelProps {
  products: ManagedProduct[]
}

export function AdminFlashSalePanel({ products }: AdminFlashSalePanelProps) {
  const [campaigns, setCampaigns] = useState<FlashSaleCampaignSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedCampaignId, setSelectedCampaignId] = useState<string | null>(null)
  const [detail, setDetail] = useState<FlashSaleCampaignDetail | null>(null)
  const [performance, setPerformance] = useState<FlashSalePerformance | null>(null)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // Create Campaign Form State
  const [showCreateForm, setShowCreateForm] = useState(false)
  const [newCampName, setNewCampName] = useState('')
  const [newStartsAt, setNewStartsAt] = useState('')
  const [newEndsAt, setNewEndsAt] = useState('')

  // Add Offer Form State
  const [selectedSkuId, setSelectedSkuId] = useState('')
  const [salePrice, setSalePrice] = useState('')
  const [quotaTotal, setQuotaTotal] = useState('10')
  const [perCustomerLimit, setPerCustomerLimit] = useState('2')

  // Cancel reason
  const [cancelReason, setCancelReason] = useState('')
  const [showCancelModal, setShowCancelModal] = useState(false)

  const loadCampaigns = useCallback(async () => {
    setLoading(true)
    try {
      const list = await getAdminFlashSales()
      setCampaigns(list)
      if (list.length > 0 && !selectedCampaignId) {
        setSelectedCampaignId(list[0]?.id ?? null)
      }
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải danh sách chiến dịch.' })
    } finally {
      setLoading(false)
    }
  }, [selectedCampaignId])

  const loadDetail = useCallback(async (id: string) => {
    try {
      const d = await getAdminFlashSale(id)
      setDetail(d)
      if (d.campaignStatus === 'ACTIVE' || d.campaignStatus === 'ENDED' || d.campaignStatus === 'CANCELLED') {
        const p = await getFlashSalePerformance(id)
        setPerformance(p)
      } else {
        setPerformance(null)
      }
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải chi tiết chiến dịch.' })
    }
  }, [])

  useEffect(() => {
    let ignore = false
    getAdminFlashSales().then(list => {
      if (!ignore) {
        setCampaigns(list)
        setLoading(false)
        if (list.length > 0) setSelectedCampaignId(prev => prev ?? (list[0]?.id ?? null))
      }
    }).catch(err => {
      if (!ignore) {
        setLoading(false)
        setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải danh sách chiến dịch.' })
      }
    })
    return () => { ignore = true }
  }, [])

  useEffect(() => {
    if (!selectedCampaignId) return
    let ignore = false
    getAdminFlashSale(selectedCampaignId).then(async d => {
      if (!ignore) setDetail(d)
      if (d.campaignStatus === 'ACTIVE' || d.campaignStatus === 'ENDED' || d.campaignStatus === 'CANCELLED') {
        const p = await getFlashSalePerformance(selectedCampaignId)
        if (!ignore) setPerformance(p)
      } else {
        if (!ignore) setPerformance(null)
      }
    }).catch(err => {
      if (!ignore) setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Lỗi tải chi tiết chiến dịch.' })
    })
    return () => { ignore = true }
  }, [selectedCampaignId])

  // Lọc danh sách SKU hợp lệ từ các sản phẩm PUBLISHED
  const publishedProducts = products.filter(p => p.publicationStatus === 'PUBLISHED')
  const availableSkus = publishedProducts.flatMap(p =>
    p.skus
      .filter(s => s.skuStatus === 'ACTIVE')
      .map(s => ({
        skuId: s.id,
        label: `${p.title} - ${s.optionLabel} (${s.skuCode}) - Giá gốc: ${formatPrice(s.regularPrice)}`,
        regularPrice: s.regularPrice,
      }))
  )

  const handleCreateCampaign = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newCampName.trim() || !newStartsAt || !newEndsAt) {
      setMessage({ type: 'error', text: 'Vui lòng điền đầy đủ thông tin chiến dịch.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      const created = await createFlashSale({
        name: newCampName.trim(),
        startsAt: new Date(newStartsAt).toISOString(),
        endsAt: new Date(newEndsAt).toISOString(),
      })
      setMessage({ type: 'success', text: `Tạo chiến dịch "${created.name}" thành công!` })
      setShowCreateForm(false)
      setNewCampName('')
      setNewStartsAt('')
      setNewEndsAt('')
      await loadCampaigns()
      setSelectedCampaignId(created.id)
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Tạo chiến dịch thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleAddOffer = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!selectedCampaignId || !selectedSkuId || !salePrice || !quotaTotal) {
      setMessage({ type: 'error', text: 'Vui lòng chọn SKU và nhập đầy đủ giá sale, quota.' })
      return
    }

    const priceNum = parseFloat(salePrice)
    const quotaNum = parseInt(quotaTotal, 10)
    const limitNum = parseInt(perCustomerLimit, 10) || 2

    if (isNaN(priceNum) || priceNum <= 0) {
      setMessage({ type: 'error', text: 'Giá Flash Sale phải lớn hơn 0.' })
      return
    }

    if (isNaN(quotaNum) || quotaNum <= 0) {
      setMessage({ type: 'error', text: 'Số lượng quota phải lớn hơn 0.' })
      return
    }

    setSubmitting(true)
    setMessage(null)
    try {
      await addFlashSaleOffer(selectedCampaignId, {
        productSkuId: selectedSkuId,
        salePrice: priceNum,
        quotaTotal: quotaNum,
        perCustomerLimit: limitNum,
      })
      setMessage({ type: 'success', text: 'Thêm ưu đãi SKU vào chiến dịch thành công!' })
      setSelectedSkuId('')
      setSalePrice('')
      setQuotaTotal('10')
      await loadDetail(selectedCampaignId)
      await loadCampaigns()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Thêm ưu đãi thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleDeleteOffer = async (offerId: string) => {
    if (!selectedCampaignId || !window.confirm('Bạn có chắc muốn xóa ưu đãi này?')) return
    setSubmitting(true)
    setMessage(null)
    try {
      await deleteFlashSaleOffer(selectedCampaignId, offerId)
      setMessage({ type: 'success', text: 'Đã xóa ưu đãi khỏi chiến dịch.' })
      await loadDetail(selectedCampaignId)
      await loadCampaigns()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Xóa ưu đãi thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handlePublish = async () => {
    if (!selectedCampaignId) return
    setSubmitting(true)
    setMessage(null)
    try {
      const res = await publishFlashSale(selectedCampaignId)
      setMessage({
        type: 'success',
        text: `Chiến dịch đã được công bố thành công với trạng thái: ${res.campaignStatus}!`,
      })
      await loadDetail(selectedCampaignId)
      await loadCampaigns()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Công bố chiến dịch thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const handleCancel = async () => {
    if (!selectedCampaignId) return
    setSubmitting(true)
    setMessage(null)
    try {
      await cancelFlashSale(selectedCampaignId, cancelReason.trim() || undefined)
      setMessage({
        type: 'success',
        text: 'Đã hủy chiến dịch Flash Sale. Mọi lượt mua mới đã bị dừng (đơn cũ bảo toàn).',
      })
      setShowCancelModal(false)
      setCancelReason('')
      await loadDetail(selectedCampaignId)
      await loadCampaigns()
    } catch (err: unknown) {
      setMessage({ type: 'error', text: err instanceof Error ? err.message : 'Hủy chiến dịch thất bại.' })
    } finally {
      setSubmitting(false)
    }
  }

  const renderStatusBadge = (status: string) => {
    switch (status) {
      case 'DRAFT':
        return <span className="status-badge" style={{ backgroundColor: '#e2e8f0', color: '#475569' }}>DRAFT</span>
      case 'SCHEDULED':
        return <span className="status-badge" style={{ backgroundColor: '#dbeafe', color: '#1e40af' }}>SCHEDULED</span>
      case 'ACTIVE':
        return <span className="status-badge" style={{ backgroundColor: '#dcfce7', color: '#166534' }}>ACTIVE</span>
      case 'ENDED':
        return <span className="status-badge" style={{ backgroundColor: '#f1f5f9', color: '#64748b' }}>ENDED</span>
      case 'CANCELLED':
        return <span className="status-badge" style={{ backgroundColor: '#fee2e2', color: '#991b1b' }}>CANCELLED</span>
      default:
        return <span className="status-badge">{status}</span>
    }
  }

  return (
    <div className="flash-sale-panel" style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem' }}>
      {message && (
        <div
          role="alert"
          style={{
            padding: '0.75rem 1rem',
            borderRadius: '6px',
            backgroundColor: message.type === 'success' ? '#dcfce7' : '#fee2e2',
            color: message.type === 'success' ? '#166534' : '#991b1b',
            border: `1px solid ${message.type === 'success' ? '#86efac' : '#fca5a5'}`,
          }}
        >
          {message.text}
        </div>
      )}

      {/* Header & Create Button */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h2 style={{ margin: 0, fontSize: '1.25rem', fontWeight: 600 }}>Quản trị Chiến dịch Flash Sale</h2>
          <p style={{ margin: '0.25rem 0 0', color: '#64748b', fontSize: '0.875rem' }}>
            Thiết lập thời gian, số lượng ưu đãi và giá khuyến mãi. Hủy chiến dịch sẽ dừng các lượt đặt hàng mới theo ưu đãi.
          </p>
        </div>
        <button
          type="button"
          onClick={() => setShowCreateForm(!showCreateForm)}
          style={{
            padding: '0.5rem 1rem',
            backgroundColor: '#2563eb',
            color: '#fff',
            border: 'none',
            borderRadius: '6px',
            fontWeight: 500,
            cursor: 'pointer',
          }}
        >
          {showCreateForm ? 'Đóng form' : '+ Tạo chiến dịch mới'}
        </button>
      </div>

      {/* Create Campaign Form Modal / Collapse */}
      {showCreateForm && (
        <form
          onSubmit={handleCreateCampaign}
          style={{
            padding: '1.25rem',
            backgroundColor: '#f8fafc',
            border: '1px solid #cbd5e1',
            borderRadius: '8px',
            display: 'flex',
            flexDirection: 'column',
            gap: '1rem',
          }}
        >
          <h3 style={{ margin: 0, fontSize: '1.1rem' }}>Tạo mới chiến dịch Flash Sale</h3>
          <div>
            <label style={{ display: 'block', fontWeight: 500, marginBottom: '0.25rem' }}>Tên chiến dịch *</label>
            <input
              type="text"
              value={newCampName}
              onChange={e => setNewCampName(e.target.value)}
              placeholder="VD: Flash Sale Giờ Vàng 11.11"
              required
              style={{ width: '100%', padding: '0.5rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
            />
          </div>
          <div className="campaign-date-fields">
            <div>
              <label style={{ display: 'block', fontWeight: 500, marginBottom: '0.25rem' }}>Bắt đầu *</label>
              <input
                type="datetime-local"
                value={newStartsAt}
                onChange={e => setNewStartsAt(e.target.value)}
                required
                style={{ width: '100%', padding: '0.5rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
              />
            </div>
            <div>
              <label style={{ display: 'block', fontWeight: 500, marginBottom: '0.25rem' }}>Kết thúc *</label>
              <input
                type="datetime-local"
                value={newEndsAt}
                onChange={e => setNewEndsAt(e.target.value)}
                required
                style={{ width: '100%', padding: '0.5rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
              />
            </div>
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '0.5rem' }}>
            <button
              type="button"
              onClick={() => setShowCreateForm(false)}
              style={{ padding: '0.5rem 1rem', border: '1px solid #cbd5e1', borderRadius: '4px', cursor: 'pointer' }}
            >
              Hủy
            </button>
            <button
              type="submit"
              disabled={submitting}
              style={{
                padding: '0.5rem 1rem',
                backgroundColor: '#16a34a',
                color: '#fff',
                border: 'none',
                borderRadius: '4px',
                cursor: 'pointer',
              }}
            >
              {submitting ? 'Đang tạo...' : 'Tạo DRAFT'}
            </button>
          </div>
        </form>
      )}

      {/* Main Content Layout: Left List, Right Detail */}
      <div className="campaign-layout">
        {/* Campaign List */}
        <div style={{ border: '1px solid #e2e8f0', borderRadius: '8px', overflow: 'hidden' }}>
          <div style={{ padding: '0.75rem 1rem', backgroundColor: '#f8fafc', borderBottom: '1px solid #e2e8f0', fontWeight: 600 }}>
            Danh sách chiến dịch ({campaigns.length})
          </div>
          {loading ? (
            <div style={{ padding: '1.5rem', textAlign: 'center', color: '#64748b' }}>Đang tải...</div>
          ) : campaigns.length === 0 ? (
            <div style={{ padding: '1.5rem', textAlign: 'center', color: '#64748b' }}>Chưa có chiến dịch nào.</div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              {campaigns.map(c => {
                const isSelected = c.id === selectedCampaignId
                return (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => setSelectedCampaignId(c.id)}
                    style={{
                      textAlign: 'left',
                      padding: '0.75rem 1rem',
                      border: 'none',
                      borderBottom: '1px solid #f1f5f9',
                      backgroundColor: isSelected ? '#eff6ff' : '#fff',
                      borderLeft: isSelected ? '4px solid #2563eb' : '4px solid transparent',
                      cursor: 'pointer',
                    }}
                  >
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <span style={{ fontWeight: 600, color: '#1e293b' }}>{c.name}</span>
                      {renderStatusBadge(c.campaignStatus)}
                    </div>
                    <div style={{ fontSize: '0.8rem', color: '#64748b', marginTop: '0.25rem' }}>
                      {new Date(c.startsAt).toLocaleDateString()} - {new Date(c.endsAt).toLocaleDateString()}
                    </div>
                    <div style={{ fontSize: '0.75rem', color: '#94a3b8', marginTop: '0.25rem' }}>
                      Offers: {c.offersCount} | Quota: {c.totalQuota} | Đã bán: {c.totalSold}
                    </div>
                  </button>
                )
              })}
            </div>
          )}
        </div>

        {/* Campaign Detail */}
        {detail ? (
          <div style={{ border: '1px solid #e2e8f0', borderRadius: '8px', padding: '1.5rem', backgroundColor: '#fff' }}>
            {/* Header info & Actions */}
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', borderBottom: '1px solid #e2e8f0', paddingBottom: '1rem', marginBottom: '1.5rem' }}>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem' }}>
                  <h3 style={{ margin: 0, fontSize: '1.3rem' }}>{detail.name}</h3>
                  {renderStatusBadge(detail.campaignStatus)}
                </div>
                <div style={{ color: '#64748b', fontSize: '0.875rem', marginTop: '0.5rem' }}>
                  <span>Bắt đầu: <strong>{new Date(detail.startsAt).toLocaleString()}</strong></span>
                  <span style={{ margin: '0 0.5rem' }}>•</span>
                  <span>Kết thúc: <strong>{new Date(detail.endsAt).toLocaleString()}</strong></span>
                </div>
              </div>

              {/* Action Buttons */}
              <div style={{ display: 'flex', gap: '0.5rem' }}>
                {detail.campaignStatus === 'DRAFT' && (
                  <button
                    type="button"
                    onClick={handlePublish}
                    disabled={submitting || detail.offers.length === 0}
                    style={{
                      padding: '0.5rem 1rem',
                      backgroundColor: '#16a34a',
                      color: '#fff',
                      border: 'none',
                      borderRadius: '6px',
                      cursor: detail.offers.length === 0 ? 'not-allowed' : 'pointer',
                      opacity: detail.offers.length === 0 ? 0.6 : 1,
                      fontWeight: 500,
                    }}
                  >
                    {submitting ? 'Đang xử lý...' : 'Công bố (Publish)'}
                  </button>
                )}

                {(detail.campaignStatus === 'DRAFT' || detail.campaignStatus === 'SCHEDULED' || detail.campaignStatus === 'ACTIVE') && (
                  <button
                    type="button"
                    onClick={() => setShowCancelModal(true)}
                    disabled={submitting}
                    style={{
                      padding: '0.5rem 1rem',
                      backgroundColor: '#dc2626',
                      color: '#fff',
                      border: 'none',
                      borderRadius: '6px',
                      cursor: 'pointer',
                      fontWeight: 500,
                    }}
                  >
                    Hủy chiến dịch
                  </button>
                )}
              </div>
            </div>

            {/* Performance Snapshot if Active/Ended/Cancelled */}
            {performance && (
              <div className="campaign-metrics">
                <div>
                  <div style={{ fontSize: '0.75rem', color: '#64748b' }}>TỔNG QUOTA</div>
                  <div style={{ fontSize: '1.25rem', fontWeight: 700, color: '#1e293b' }}>{performance.totalQuota}</div>
                </div>
                <div>
                  <div style={{ fontSize: '0.75rem', color: '#64748b' }}>ĐANG GIỮ / ĐÃ BÁN</div>
                  <div style={{ fontSize: '1.25rem', fontWeight: 700, color: '#2563eb' }}>{performance.totalHeld} / {performance.totalSold}</div>
                </div>
                <div>
                  <div style={{ fontSize: '0.75rem', color: '#64748b' }}>QUOTA CÒN LẠI</div>
                  <div style={{ fontSize: '1.25rem', fontWeight: 700, color: '#16a34a' }}>{performance.remainingQuota}</div>
                </div>
                <div>
                  <div style={{ fontSize: '0.75rem', color: '#64748b' }}>DOANH THU ĐÃ BÁN</div>
                  <div style={{ fontSize: '1.25rem', fontWeight: 700, color: '#b45309' }}>{formatPrice(performance.estimatedRevenue)}</div>
                </div>
              </div>
            )}

            {/* Offers Management */}
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1rem' }}>
                <h4 style={{ margin: 0, fontSize: '1.1rem' }}>Danh sách ưu đãi SKU ({detail.offers.length})</h4>
              </div>

              {/* Add Offer Form (Only if DRAFT) */}
              {detail.campaignStatus === 'DRAFT' && (
                <form
                  onSubmit={handleAddOffer}
                  style={{
                    padding: '1rem',
                    backgroundColor: '#f1f5f9',
                    borderRadius: '6px',
                    marginBottom: '1.5rem',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '0.75rem',
                  }}
                >
                  <div style={{ fontWeight: 600, fontSize: '0.9rem' }}>+ Thêm sản phẩm biến thể (SKU) vào Flash Sale</div>
                  <div className="campaign-offer-fields">
                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 500, marginBottom: '0.25rem' }}>Chọn SKU *</label>
                      <select
                        value={selectedSkuId}
                        onChange={e => {
                          setSelectedSkuId(e.target.value)
                          const chosen = availableSkus.find(s => s.skuId === e.target.value)
                          if (chosen) {
                            // Gợi ý giá sale = 80% regular price
                            setSalePrice(Math.round(chosen.regularPrice * 0.8).toString())
                          }
                        }}
                        required
                        style={{ width: '100%', padding: '0.45rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
                      >
                        <option value="">-- Chọn SKU hợp lệ --</option>
                        {availableSkus.map(s => (
                          <option key={s.skuId} value={s.skuId}>{s.label}</option>
                        ))}
                      </select>
                    </div>

                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 500, marginBottom: '0.25rem' }}>Giá Flash Sale *</label>
                      <input
                        type="number"
                        min="1"
                        step="1"
                        value={salePrice}
                        onChange={e => setSalePrice(e.target.value)}
                        placeholder="VD: 150000"
                        required
                        style={{ width: '100%', padding: '0.45rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
                      />
                    </div>

                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 500, marginBottom: '0.25rem' }}>Quota mở bán *</label>
                      <input
                        type="number"
                        min="1"
                        value={quotaTotal}
                        onChange={e => setQuotaTotal(e.target.value)}
                        required
                        style={{ width: '100%', padding: '0.45rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
                      />
                    </div>

                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 500, marginBottom: '0.25rem' }}>Max/Khách</label>
                      <input
                        type="number"
                        min="1"
                        max="99"
                        value={perCustomerLimit}
                        onChange={e => setPerCustomerLimit(e.target.value)}
                        required
                        style={{ width: '100%', padding: '0.45rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
                      />
                    </div>

                    <button
                      type="submit"
                      disabled={submitting}
                      style={{
                        padding: '0.5rem 1rem',
                        backgroundColor: '#2563eb',
                        color: '#fff',
                        border: 'none',
                        borderRadius: '4px',
                        cursor: 'pointer',
                        fontWeight: 500,
                      }}
                    >
                      {submitting ? '...' : 'Thêm'}
                    </button>
                  </div>
                </form>
              )}

              {/* Offers Table */}
              {detail.offers.length === 0 ? (
                <div style={{ padding: '2rem', textAlign: 'center', color: '#94a3b8', border: '1px dashed #cbd5e1', borderRadius: '6px' }}>
                  Chưa có sản phẩm nào trong chiến dịch này. Vui lòng thêm ít nhất 1 SKU để công bố.
                </div>
              ) : (
                <div className="table-responsive"><table className="admin-table">
                  <thead>
                    <tr style={{ backgroundColor: '#f8fafc', borderBottom: '1px solid #e2e8f0' }}>
                      <th style={{ padding: '0.75rem' }}>Sản phẩm / SKU</th>
                      <th style={{ padding: '0.75rem' }}>Giá gốc</th>
                      <th style={{ padding: '0.75rem' }}>Giá Flash Sale</th>
                      <th style={{ padding: '0.75rem' }}>Quota</th>
                      <th style={{ padding: '0.75rem' }}>Đã bán / Còn lại</th>
                      <th style={{ padding: '0.75rem' }}>Giới hạn/Khách</th>
                      {detail.campaignStatus === 'DRAFT' && <th style={{ padding: '0.75rem', textAlign: 'right' }}>Thao tác</th>}
                    </tr>
                  </thead>
                  <tbody>
                    {detail.offers.map(o => (
                      <tr key={o.id} style={{ borderBottom: '1px solid #f1f5f9' }}>
                        <td style={{ padding: '0.75rem' }}>
                          <div style={{ fontWeight: 600 }}>{o.productTitle}</div>
                          <div style={{ color: '#64748b', fontSize: '0.75rem' }}>{o.skuCode} - {o.optionLabel}</div>
                        </td>
                        <td style={{ padding: '0.75rem', textDecoration: 'line-through', color: '#94a3b8' }}>
                          {formatPrice(o.regularPrice)}
                        </td>
                        <td style={{ padding: '0.75rem', fontWeight: 600, color: '#dc2626' }}>
                          {formatPrice(o.salePrice)}
                        </td>
                        <td style={{ padding: '0.75rem' }}>{o.quotaTotal}</td>
                        <td style={{ padding: '0.75rem' }}>
                          <span style={{ color: '#16a34a', fontWeight: 600 }}>{o.quotaSold}</span> / {o.remainingQuota}
                        </td>
                        <td style={{ padding: '0.75rem' }}>{o.perCustomerLimit}</td>
                        {detail.campaignStatus === 'DRAFT' && (
                          <td style={{ padding: '0.75rem', textAlign: 'right' }}>
                            <button
                              type="button"
                              onClick={() => handleDeleteOffer(o.id)}
                              disabled={submitting}
                              style={{
                                padding: '0.25rem 0.5rem',
                                color: '#dc2626',
                                background: 'transparent',
                                border: '1px solid #fca5a5',
                                borderRadius: '4px',
                                cursor: 'pointer',
                              }}
                            >
                              Xóa
                            </button>
                          </td>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table></div>
              )}
            </div>
          </div>
        ) : (
          <div style={{ padding: '3rem', textAlign: 'center', color: '#94a3b8', border: '1px dashed #e2e8f0', borderRadius: '8px' }}>
            Chọn một chiến dịch từ danh sách bên trái để xem và quản lý chi tiết.
          </div>
        )}
      </div>

      {/* Cancel Confirmation Modal */}
      {showCancelModal && (
        <div
          role="dialog"
          style={{
            position: 'fixed',
            inset: 0,
            backgroundColor: 'rgba(0,0,0,0.5)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            zIndex: 1000,
          }}
        >
          <div
            style={{
              backgroundColor: '#fff',
              padding: '1.5rem',
              borderRadius: '8px',
              maxWidth: '480px',
              width: '90%',
              display: 'flex',
              flexDirection: 'column',
              gap: '1rem',
            }}
          >
            <h3 style={{ margin: 0, color: '#dc2626' }}>Xác nhận HỦY chiến dịch Flash Sale</h3>
            <p style={{ margin: 0, color: '#475569', fontSize: '0.875rem' }}>
              Hành động này sẽ chuyển trạng thái chiến dịch thành <strong>CANCELLED</strong> và dừng toàn bộ các lượt mua mới.
              Theo nguyên tắc bất biến <strong>SM-Q02</strong>, các đơn hàng đã tạo hoặc thanh toán trước đó vẫn được bảo toàn nguyên vẹn.
            </p>
            <div>
              <label style={{ display: 'block', fontSize: '0.875rem', fontWeight: 500, marginBottom: '0.25rem' }}>
                Lý do hủy (tùy chọn):
              </label>
              <textarea
                value={cancelReason}
                onChange={e => setCancelReason(e.target.value)}
                placeholder="VD: Điều chỉnh lịch khuyến mãi..."
                rows={3}
                style={{ width: '100%', padding: '0.5rem', borderRadius: '4px', border: '1px solid #cbd5e1' }}
              />
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '0.5rem' }}>
              <button
                type="button"
                onClick={() => setShowCancelModal(false)}
                style={{ padding: '0.5rem 1rem', border: '1px solid #cbd5e1', borderRadius: '4px', cursor: 'pointer' }}
              >
                Đóng
              </button>
              <button
                type="button"
                onClick={handleCancel}
                disabled={submitting}
                style={{
                  padding: '0.5rem 1rem',
                  backgroundColor: '#dc2626',
                  color: '#fff',
                  border: 'none',
                  borderRadius: '4px',
                  cursor: 'pointer',
                  fontWeight: 500,
                }}
              >
                {submitting ? 'Đang hủy...' : 'Xác nhận Hủy'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
