import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { type AdminCategory, categoryErrorText, createAdminCategory, deleteAdminCategory, listAdminCategories, slugifyCategory, updateAdminCategory } from './category-management.api'

export function AdminCategoriesPanel() {
  const [items, setItems] = useState<AdminCategory[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<AdminCategory | null>(null)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [isVisible, setIsVisible] = useState(true)
  const [deleting, setDeleting] = useState<AdminCategory | null>(null)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const load = useCallback(async () => {
    try { setItems(await listAdminCategories()) }
    catch (error) { setMessage({ kind: 'error', text: categoryErrorText(error, 'Không tải được danh mục.') }) }
    finally { setLoading(false) }
  }, [])
  useEffect(() => {
    let ignore = false
    listAdminCategories()
      .then(data => { if (!ignore) setItems(data) })
      .catch(error => { if (!ignore) setMessage({ kind: 'error', text: categoryErrorText(error, 'Không tải được danh mục.') }) })
      .finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [])
  const visibleItems = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase('vi')
    return normalized ? items.filter(item => `${item.name} ${item.slug}`.toLocaleLowerCase('vi').includes(normalized)) : items
  }, [items, query])

  function resetForm() { setEditing(null); setName(''); setSlug(''); setSlugTouched(false); setIsVisible(true) }
  function beginEdit(item: AdminCategory) { setEditing(item); setName(item.name); setSlug(item.slug); setSlugTouched(true); setIsVisible(item.isVisible); setMessage(null) }
  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy(true); setMessage(null)
    try {
      const payload = { name: name.trim(), slug: slug.trim(), isVisible }
      if (editing) await updateAdminCategory(editing.id, payload); else await createAdminCategory(payload)
      setMessage({ kind: 'success', text: editing ? 'Đã cập nhật danh mục và ghi Audit Log.' : 'Đã tạo danh mục mới.' })
      resetForm(); await load()
    } catch (error) { setMessage({ kind: 'error', text: categoryErrorText(error, 'Không thể lưu danh mục.') }) }
    finally { setBusy(false) }
  }
  async function toggle(item: AdminCategory) {
    setBusy(true); setMessage(null)
    try {
      await updateAdminCategory(item.id, { name: item.name, slug: item.slug, isVisible: !item.isVisible })
      setMessage({ kind: 'success', text: item.isVisible ? 'Đã ẩn danh mục khỏi Storefront.' : 'Đã hiển thị lại danh mục.' }); await load()
    } catch (error) { setMessage({ kind: 'error', text: categoryErrorText(error, 'Không thể đổi trạng thái.') }) }
    finally { setBusy(false) }
  }
  async function remove() {
    if (!deleting) return
    setBusy(true); setMessage(null)
    try { await deleteAdminCategory(deleting.id); setMessage({ kind: 'success', text: `Đã xóa danh mục “${deleting.name}”.` }); setDeleting(null); await load() }
    catch (error) { setMessage({ kind: 'error', text: categoryErrorText(error, 'Không thể xóa danh mục.') }); setDeleting(null) }
    finally { setBusy(false) }
  }

  return <section className="management-layout category-management" aria-labelledby="category-management-title">
    {deleting && <ConfirmDialog title="Xóa danh mục trống?" description={`Danh mục “${deleting.name}” sẽ bị xóa vĩnh viễn. Danh mục có sản phẩm sẽ được hệ thống chặn xóa.`} confirmLabel="Xóa danh mục" busy={busy} onCancel={() => setDeleting(null)} onConfirm={() => void remove()} />}
    <div className="management-card">
      <span className="panel-kicker">Taxonomy · Storefront</span><h2 id="category-management-title">Danh mục sản phẩm</h2>
      <p className="panel-description">Điều khiển bộ lọc và điều hướng công khai. Ẩn danh mục không gỡ sản phẩm khỏi bán.</p>
      <label className="workspace-search">Tìm danh mục<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Tên hoặc slug" /></label>
      {loading ? <p role="status">Đang tải danh mục…</p> : <div className="category-admin-list">
        {visibleItems.map(item => <article className="category-admin-row" key={item.id}>
          <div><strong>{item.name}</strong><code>/{item.slug}</code><small>{item.productCount} sản phẩm</small></div>
          <span className={`status-badge ${item.isVisible ? 'badge-success' : 'badge-muted'}`}>{item.isVisible ? 'Đang hiển thị' : 'Đang ẩn'}</span>
          <div className="management-card__actions">
            <button type="button" className="button-secondary" disabled={busy} onClick={() => beginEdit(item)}>Sửa</button>
            <button type="button" className="button-secondary" disabled={busy} onClick={() => void toggle(item)}>{item.isVisible ? 'Ẩn' : 'Hiện'}</button>
            <button type="button" className="text-button danger" disabled={busy || item.productCount > 0} title={item.productCount > 0 ? 'Danh mục đang có sản phẩm' : undefined} onClick={() => setDeleting(item)}>Xóa</button>
          </div>
        </article>)}
        {visibleItems.length === 0 && <div className="empty-state"><strong>Không có danh mục phù hợp</strong><p>Thử từ khóa khác hoặc tạo danh mục mới.</p></div>}
      </div>}
    </div>
    <div className="management-card category-editor-card">
      <span className="panel-kicker">{editing ? 'Chỉnh sửa' : 'Tạo mới'}</span><h2>{editing ? editing.name : 'Danh mục mới'}</h2>
      {message && <div className={`alert ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}>{message.text}</div>}
      <form onSubmit={submit} className="category-editor-form">
        <label>Tên danh mục<input required minLength={2} maxLength={180} value={name} onChange={event => { const value = event.target.value; setName(value); if (!slugTouched) setSlug(slugifyCategory(value)) }} placeholder="Ví dụ: Túi và balo cầu lông" /></label>
        <label>Slug URL<input required minLength={2} maxLength={220} pattern="[a-z0-9]+(?:-[a-z0-9]+)*" value={slug} onChange={event => { setSlugTouched(true); setSlug(event.target.value.toLowerCase()) }} placeholder="tui-va-balo-cau-long" /></label>
        <label className="checkbox-row"><input type="checkbox" checked={isVisible} onChange={event => setIsVisible(event.target.checked)} />Hiển thị trong bộ lọc Storefront</label>
        <div className="management-card__actions"><button type="submit" className="button-primary" disabled={busy}>{busy ? 'Đang lưu…' : editing ? 'Lưu thay đổi' : 'Tạo danh mục'}</button>{editing && <button type="button" className="button-secondary" onClick={resetForm}>Hủy sửa</button>}</div>
      </form>
    </div>
  </section>
}
