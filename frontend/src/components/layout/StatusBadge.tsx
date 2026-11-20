const labels: Record<string, string> = {
  DRAFT: 'Bản nháp', PUBLISHED: 'Đã công bố', ACTIVE: 'Đang hoạt động', INACTIVE: 'Ngừng hoạt động',
  PENDING_COD_CONFIRMATION: 'Chờ xác nhận', CONFIRMED: 'Đã xác nhận', FULFILLING: 'Đang xử lý',
  COMPLETED: 'Hoàn tất', CANCELLED: 'Đã hủy', COD_PENDING: 'Thanh toán khi nhận hàng',
  UNPAID: 'Chưa thanh toán', PAID: 'Đã thanh toán', VOID: 'Đã đóng',
  APPLIED: 'Đã áp dụng', REJECTED: 'Đã từ chối', PENDING: 'Chờ duyệt',
}

export function StatusBadge({ value, label }: { value: string; label?: string }) {
  const normalized = value.toUpperCase()
  const tone = ['COMPLETED', 'PAID', 'PUBLISHED', 'ACTIVE', 'CONFIRMED', 'APPLIED', 'SUCCESS'].includes(normalized) ? 'success'
    : ['CANCELLED', 'VOID', 'INACTIVE', 'REJECTED', 'FAILED'].includes(normalized) ? 'danger'
    : ['PENDING', 'PENDING_COD_CONFIRMATION', 'COD_PENDING', 'UNPAID', 'FULFILLING'].includes(normalized) ? 'pending'
    : 'neutral'
  return <span className={`status-badge status-${tone}`}>{label ?? labels[value] ?? value}</span>
}
