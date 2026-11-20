import { useEffect, useRef } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { useComparison } from './useComparison'
import { buildComparisonUrl, resolveComparisonImageUrl } from './comparison-storage'
import './comparison.css'

export function ComparisonTray() {
  const { pathname } = useLocation()
  const { items, count, notice, remove, clear } = useComparison()
  const removeButtons = useRef(new Map<string, HTMLButtonElement>())
  const pendingFocusIndex = useRef<number | null>(null)
  const hidden = pathname === '/admin' || pathname === '/staff' || pathname === '/compare'

  useEffect(() => {
    if (pendingFocusIndex.current === null || items.length === 0) return
    const target = items[Math.min(pendingFocusIndex.current, items.length - 1)]
    pendingFocusIndex.current = null
    if (target) removeButtons.current.get(target.id)?.focus()
  }, [items])

  if (hidden || count === 0) return null

  function handleRemove(productId: string, index: number) {
    pendingFocusIndex.current = index
    remove(productId)
  }

  return (
    <aside className="comparison-tray" aria-label="Danh sách sản phẩm so sánh">
      <div className="comparison-tray-heading">
        <div><strong>So sánh sản phẩm</strong><span>{count}/4 sản phẩm</span></div>
        <button type="button" className="comparison-clear" onClick={clear}>Xóa tất cả</button>
      </div>
      <div className="comparison-tray-items">
        {items.map(item => (
          <div className="comparison-tray-item" key={item.id}>
            {resolveComparisonImageUrl(item.imageUrl) ? <img src={resolveComparisonImageUrl(item.imageUrl)!} alt="" /> : <span className="comparison-thumb-placeholder" aria-hidden="true">↔</span>}
            <span title={item.title}>{item.title}</span>
            <button
              ref={node => { if (node) removeButtons.current.set(item.id, node); else removeButtons.current.delete(item.id) }}
              type="button"
              onClick={() => handleRemove(item.id, items.indexOf(item))}
              aria-label={`Bỏ ${item.title} khỏi so sánh`}
            >×</button>
          </div>
        ))}
      </div>
      <div className="comparison-tray-actions">
        {count < 2 ? (
          <span className="comparison-pending-route" aria-disabled="true">Chọn thêm ít nhất 1 sản phẩm</span>
        ) : (
          <Link className="comparison-open" to={buildComparisonUrl(items)}>So sánh ngay ({count})</Link>
        )}
      </div>
      {notice && <p className="comparison-notice" role="status">{notice}</p>}
    </aside>
  )
}
