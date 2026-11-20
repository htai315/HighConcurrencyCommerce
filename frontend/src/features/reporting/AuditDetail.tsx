import type { AuditLogItem } from './reporting.api'

function readable(value: string | null) {
  if (value === null) return 'Không có dữ liệu được ghi nhận.'
  try { return JSON.stringify(JSON.parse(value), null, 2) }
  catch { return value }
}

export function AuditDetail({ item }: { item: AuditLogItem }) {
  return <details className="audit-detail">
    <summary aria-label={`Xem thay đổi ${item.action} lúc ${new Date(item.occurredAt).toLocaleString('vi-VN')}`}>Xem thay đổi</summary>
    <dl><div><dt>Mã bản ghi</dt><dd>{item.id}</dd></div><div><dt>Mã đối chiếu</dt><dd>{item.correlationId ?? 'Không có'}</dd></div></dl>
    <section aria-label="Dữ liệu trước thay đổi"><h3>Trước thay đổi</h3><pre>{readable(item.beforeData)}</pre></section>
    <section aria-label="Dữ liệu sau thay đổi"><h3>Sau thay đổi</h3><pre>{readable(item.afterData)}</pre></section>
  </details>
}
