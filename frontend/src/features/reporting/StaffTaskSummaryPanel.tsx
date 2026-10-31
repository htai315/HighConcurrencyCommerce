import { useEffect, useState } from 'react'
import { getStaffTasks } from './reporting.api'
import type { StaffTasks } from './reporting.api'

export function StaffTaskSummaryPanel() {
  const [tasks, setTasks] = useState<StaffTasks | null>(null)
  const [error, setError] = useState(false)
  useEffect(() => {
    let ignore = false
    getStaffTasks().then(value => { if (!ignore) setTasks(value) }).catch(() => { if (!ignore) setError(true) })
    return () => { ignore = true }
  }, [])
  if (error) return <div className="management-card"><p role="alert">Không tải được danh sách tác vụ.</p></div>
  if (!tasks) return <div className="management-card"><p role="status">Đang tổng hợp tác vụ…</p></div>
  const cards = [
    ['COD chờ xác nhận', tasks.pendingCodCount], ['Chờ đóng gói', tasks.shipmentsToPackCount],
    ['Chờ bàn giao', tasks.shipmentsToHandoverCount], ['Giao thất bại', tasks.deliveryFailuresCount],
    ['SKU tồn thấp', tasks.lowStockSkuCount], ['Thông báo chưa đọc', tasks.notificationsUnreadCount],
  ] as const
  return <div className="management-card"><h2>Tác vụ cần xử lý</h2><p className="page-intro">Số liệu thời điểm hiện tại, đọc trực tiếp từ nguồn sự thật PostgreSQL.</p>
    <div className="report-card-grid">{cards.map(([label, count]) => <article className="metric-card" key={label}><span>{label}</span><strong>{count}</strong></article>)}</div>
  </div>
}
