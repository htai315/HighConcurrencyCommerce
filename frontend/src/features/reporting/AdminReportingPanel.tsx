import { useEffect, useState } from 'react'
import { formatPrice } from '../catalog/catalog.api'
import { getAuditLogs, getLowStock, getRevenue } from './reporting.api'
import type { AuditPage, LowStockReport, RevenueReport } from './reporting.api'
import { AuditDetail } from './AuditDetail'

export function AdminReportingPanel() {
  const [revenue, setRevenue] = useState<RevenueReport | null>(null)
  const [lowStock, setLowStock] = useState<LowStockReport | null>(null)
  const [audit, setAudit] = useState<AuditPage | null>(null)
  const [action, setAction] = useState('')
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState(false)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [reportError, setReportError] = useState<string | null>(null)
  const [appliedAction, setAppliedAction] = useState('')
  const [reload, setReload] = useState(0)
  useEffect(() => {
    let ignore = false
    Promise.all([getRevenue(), getLowStock(), getAuditLogs()]).then(([revenueResult, stockResult, auditResult]) => {
      if (!ignore) { setRevenue(revenueResult); setLowStock(stockResult); setAudit(auditResult) }
    }).catch(() => { if (!ignore) setError(true) })
    return () => { ignore = true }
  }, [reload])
  async function filterAudit(page = 1, filter = action) {
    setError(false)
    setBusy(true)
    try { setAudit(await getAuditLogs(filter, page)); setAppliedAction(filter) } catch { setError(true) }
    finally { setBusy(false) }
  }
  async function filterRevenue(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (from && to && from > to) { setReportError('Ngày kết thúc phải từ ngày bắt đầu trở đi.'); return }
    setBusy(true); setReportError(null)
    try { setRevenue(await getRevenue(from, to)) }
    catch { setReportError('Không tải được doanh thu trong khoảng ngày đã chọn. Vui lòng thử lại.'); }
    finally { setBusy(false) }
  }
  if (error && !revenue) return <div className="management-card"><p role="alert">Không tải được dữ liệu báo cáo.</p><button type="button" className="button-secondary" onClick={() => { setError(false); setReload(value => value + 1) }}>Thử lại</button></div>
  if (!revenue || !lowStock || !audit) return <div className="management-card"><p role="status">Đang tổng hợp báo cáo…</p></div>
  return <div className="reporting-stack">
    <form className="report-period" onSubmit={filterRevenue}>
      <div><h2>Hiệu quả kinh doanh</h2><p>Doanh thu từ những đơn đã hoàn tất hợp lệ.</p></div>
      <label>Từ ngày<input type="date" value={from} onChange={event => setFrom(event.target.value)} /></label>
      <label>Đến ngày<input type="date" value={to} min={from || undefined} onChange={event => setTo(event.target.value)} /></label>
      <button type="submit" className="button-primary" disabled={busy}>Xem báo cáo</button>
    </form>
    {reportError && <p role="alert" className="alert">{reportError}</p>}
    {error && <p role="alert" className="alert">Không thể cập nhật Audit Log.</p>}
    <div className="report-card-grid reporting-kpis">
      <article className="metric-card"><span>Doanh thu gộp</span><strong>{formatPrice(revenue.grossRevenue)}</strong><small>{revenue.from} → {revenue.to}</small></article>
      <article className="metric-card"><span>Tiền đã hoàn</span><strong>{formatPrice(revenue.refundAmount)}</strong></article>
      <article className="metric-card"><span>Doanh thu thuần</span><strong>{formatPrice(revenue.netRevenue)}</strong><small>Có thể âm theo kỳ kế toán</small></article>
      <article className="metric-card"><span>Đơn hoàn tất hợp lệ</span><strong>{revenue.completedOrderCount}</strong></article>
      <article className="metric-card"><span>Giá trị trung bình</span><strong>{formatPrice(revenue.averageOrderValue)}</strong></article>
      <article className="metric-card"><span>SKU tồn ≤ {lowStock.threshold}</span><strong>{lowStock.items.length}</strong></article>
    </div>
    <div className="management-card"><h2>Tồn kho dưới ngưỡng</h2><div className="table-responsive"><table className="admin-table"><thead><tr><th>SKU</th><th>Sản phẩm</th><th>Khả dụng</th><th>Vật lý</th><th>Reserved</th><th>Allocated</th></tr></thead>
      <tbody>{lowStock.items.length === 0 ? <tr><td colSpan={6}>Không có SKU tồn thấp.</td></tr> : lowStock.items.map(item => <tr key={item.productSkuId}><td><strong>{item.skuCode}</strong><br />{item.optionLabel}</td><td>{item.productTitle}</td><td>{item.availableQty}</td><td>{item.physicalQty}</td><td>{item.reservedQty}</td><td>{item.allocatedQty}</td></tr>)}</tbody></table></div></div>
    <details className="management-card revenue-breakdown"><summary>Doanh thu theo ngày ({revenue.days.length})</summary><div className="table-responsive"><table className="admin-table"><thead><tr><th>Ngày</th><th>Đơn hoàn tất</th><th>Doanh thu gộp</th><th>Hoàn tiền</th><th>Doanh thu thuần</th></tr></thead><tbody>{revenue.days.length ? revenue.days.map(day => <tr key={day.date}><td>{day.date.split('-').reverse().join('/')}</td><td>{day.completedOrderCount}</td><td>{formatPrice(day.grossRevenue)}</td><td>{formatPrice(day.refundAmount)}</td><td>{formatPrice(day.netRevenue)}</td></tr>) : <tr><td colSpan={5}>Chưa có doanh thu hoặc hoàn tiền trong khoảng thời gian này.</td></tr>}</tbody></table></div></details>
    <div className="management-card"><form className="report-filter" onSubmit={event => { event.preventDefault(); void filterAudit() }}><div><h2>Audit Log ({audit.totalCount})</h2><p>Nhật ký thao tác quản trị.</p></div><label>Thao tác<input list="audit-actions" value={action} onChange={event => setAction(event.target.value)} placeholder="Tất cả thao tác" /><datalist id="audit-actions"><option value="PRODUCT_PUBLISHED">Công bố sản phẩm</option><option value="SKU_PRICE_UPDATED">Cập nhật giá</option></datalist></label><button type="submit" className="button-secondary" disabled={busy}>Lọc Audit</button></form>
      <div className="table-responsive" role="region" aria-label="Nhật ký thao tác" tabIndex={0} aria-busy={busy}><table className="admin-table"><thead><tr><th>Thời gian</th><th>Người thực hiện</th><th>Thao tác</th><th>Đối tượng</th><th>Chi tiết</th></tr></thead><tbody>{audit.items.map(item => <tr key={item.id}><td>{new Date(item.occurredAt).toLocaleString('vi-VN')}</td><td>{item.actorName ?? item.actorType}</td><td><strong>{item.action}</strong></td><td>{item.entityType}<br /><code>{item.entityId}</code></td><td><AuditDetail item={item} /></td></tr>)}</tbody></table></div>
      {audit.items.length === 0 && <p className="report-empty">Không có thao tác phù hợp với bộ lọc hiện tại.</p>}
      <nav className="data-pagination" aria-label="Phân trang nhật ký"><button className="button-secondary" disabled={busy || audit.page <= 1} onClick={() => void filterAudit(audit.page - 1, appliedAction)}>Trang trước</button><span>Trang {audit.page}</span><button className="button-secondary" disabled={busy || audit.page * audit.pageSize >= audit.totalCount} onClick={() => void filterAudit(audit.page + 1, appliedAction)}>Trang sau</button></nav>
    </div>
  </div>
}
