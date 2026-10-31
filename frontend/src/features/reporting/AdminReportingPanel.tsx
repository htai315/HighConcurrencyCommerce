import { useEffect, useState } from 'react'
import { formatPrice } from '../catalog/catalog.api'
import { getAuditLogs, getLowStock, getRevenue } from './reporting.api'
import type { AuditPage, LowStockReport, RevenueReport } from './reporting.api'

export function AdminReportingPanel() {
  const [revenue, setRevenue] = useState<RevenueReport | null>(null)
  const [lowStock, setLowStock] = useState<LowStockReport | null>(null)
  const [audit, setAudit] = useState<AuditPage | null>(null)
  const [action, setAction] = useState('')
  const [error, setError] = useState(false)
  useEffect(() => {
    let ignore = false
    Promise.all([getRevenue(), getLowStock(), getAuditLogs()]).then(([revenueResult, stockResult, auditResult]) => {
      if (!ignore) { setRevenue(revenueResult); setLowStock(stockResult); setAudit(auditResult) }
    }).catch(() => { if (!ignore) setError(true) })
    return () => { ignore = true }
  }, [])
  async function filterAudit() {
    setError(false)
    try { setAudit(await getAuditLogs(action)) } catch { setError(true) }
  }
  if (error && !revenue) return <div className="management-card"><p role="alert">Không tải được dữ liệu báo cáo.</p></div>
  if (!revenue || !lowStock || !audit) return <div className="management-card"><p role="status">Đang tổng hợp báo cáo…</p></div>
  return <div className="reporting-stack">
    {error && <p role="alert" className="alert">Không thể cập nhật Audit Log.</p>}
    <div className="report-card-grid">
      <article className="metric-card"><span>Doanh thu ghi nhận</span><strong>{formatPrice(revenue.revenue)}</strong><small>{revenue.from} → {revenue.to}</small></article>
      <article className="metric-card"><span>Đơn hoàn tất hợp lệ</span><strong>{revenue.completedOrderCount}</strong></article>
      <article className="metric-card"><span>Giá trị trung bình</span><strong>{formatPrice(revenue.averageOrderValue)}</strong></article>
      <article className="metric-card"><span>SKU tồn ≤ {lowStock.threshold}</span><strong>{lowStock.items.length}</strong></article>
    </div>
    <div className="management-card"><h2>Tồn kho dưới ngưỡng</h2><div className="table-responsive"><table className="admin-table"><thead><tr><th>SKU</th><th>Sản phẩm</th><th>Khả dụng</th><th>Vật lý</th><th>Reserved</th><th>Allocated</th></tr></thead>
      <tbody>{lowStock.items.length === 0 ? <tr><td colSpan={6}>Không có SKU tồn thấp.</td></tr> : lowStock.items.map(item => <tr key={item.productSkuId}><td><strong>{item.skuCode}</strong><br />{item.optionLabel}</td><td>{item.productTitle}</td><td>{item.availableQty}</td><td>{item.physicalQty}</td><td>{item.reservedQty}</td><td>{item.allocatedQty}</td></tr>)}</tbody></table></div></div>
    <div className="management-card"><div className="report-filter"><div><h2>Audit Log ({audit.totalCount})</h2><p>Tra cứu theo action chính xác.</p></div><label>Action<input value={action} onChange={event => setAction(event.target.value)} placeholder="VD: PRODUCT_PUBLISHED" /></label><button type="button" className="button-secondary" onClick={() => void filterAudit()}>Lọc Audit</button></div>
      <div className="table-responsive"><table className="admin-table"><thead><tr><th>Thời gian</th><th>Actor</th><th>Action</th><th>Entity</th><th>Correlation</th></tr></thead><tbody>{audit.items.map(item => <tr key={item.id}><td>{new Date(item.occurredAt).toLocaleString('vi-VN')}</td><td>{item.actorName ?? item.actorType}</td><td><strong>{item.action}</strong></td><td>{item.entityType}<br /><code>{item.entityId}</code></td><td>{item.correlationId ?? '—'}</td></tr>)}</tbody></table></div>
    </div>
  </div>
}
