import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'
import { brandError, createAdminBrand, deleteAdminBrand, listAdminBrands, slugifyBrand, type AdminBrand, updateAdminBrand } from './brand-management.api'

export function AdminBrandsPanel() {
  const [items, setItems] = useState<AdminBrand[]>([]); const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false)
  const [query, setQuery] = useState(''); const [editing, setEditing] = useState<AdminBrand | null>(null); const [deleting, setDeleting] = useState<AdminBrand | null>(null)
  const [name, setName] = useState(''); const [slug, setSlug] = useState(''); const [slugTouched, setSlugTouched] = useState(false); const [country, setCountry] = useState(''); const [websiteUrl, setWebsiteUrl] = useState(''); const [isActive, setIsActive] = useState(true)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)
  const load = useCallback(async () => { try { setItems(await listAdminBrands()) } catch (error) { setMessage({ kind: 'error', text: brandError(error, 'Không tải được thương hiệu.') }) } finally { setLoading(false) } }, [])
  useEffect(() => {
    let ignore = false
    listAdminBrands().then(data => { if (!ignore) setItems(data) }).catch(error => { if (!ignore) setMessage({ kind: 'error', text: brandError(error, 'Không tải được thương hiệu.') }) }).finally(() => { if (!ignore) setLoading(false) })
    return () => { ignore = true }
  }, [])
  const visible = useMemo(() => { const q = query.trim().toLocaleLowerCase('vi'); return q ? items.filter(x => `${x.name} ${x.slug} ${x.country ?? ''}`.toLocaleLowerCase('vi').includes(q)) : items }, [items, query])
  function reset() { setEditing(null); setName(''); setSlug(''); setSlugTouched(false); setCountry(''); setWebsiteUrl(''); setIsActive(true) }
  function edit(x: AdminBrand) { setEditing(x); setName(x.name); setSlug(x.slug); setSlugTouched(true); setCountry(x.country ?? ''); setWebsiteUrl(x.websiteUrl ?? ''); setIsActive(x.isActive); setMessage(null) }
  async function submit(event: FormEvent) { event.preventDefault(); setBusy(true); setMessage(null); try { const body = { name: name.trim(), slug: slug.trim(), country: country.trim() || undefined, websiteUrl: websiteUrl.trim() || undefined, isActive }; if (editing) await updateAdminBrand(editing.id, body); else await createAdminBrand(body); setMessage({ kind: 'success', text: editing ? 'Đã cập nhật thương hiệu.' : 'Đã tạo thương hiệu.' }); reset(); await load() } catch (error) { setMessage({ kind: 'error', text: brandError(error, 'Không thể lưu thương hiệu.') }) } finally { setBusy(false) } }
  async function toggle(x: AdminBrand) { setBusy(true); try { await updateAdminBrand(x.id, { name: x.name, slug: x.slug, country: x.country ?? undefined, websiteUrl: x.websiteUrl ?? undefined, isActive: !x.isActive }); await load() } catch (error) { setMessage({ kind: 'error', text: brandError(error, 'Không thể đổi trạng thái.') }) } finally { setBusy(false) } }
  async function remove() { if (!deleting) return; setBusy(true); try { await deleteAdminBrand(deleting.id); setMessage({ kind: 'success', text: `Đã xóa “${deleting.name}”.` }); setDeleting(null); await load() } catch (error) { setMessage({ kind: 'error', text: brandError(error, 'Không thể xóa thương hiệu.') }); setDeleting(null) } finally { setBusy(false) } }
  return <section className="management-layout category-management" aria-labelledby="brand-management-title">
    {deleting && <ConfirmDialog title="Xóa thương hiệu?" description={`Thương hiệu “${deleting.name}” chỉ có thể xóa khi chưa gắn sản phẩm.`} confirmLabel="Xóa thương hiệu" busy={busy} onCancel={() => setDeleting(null)} onConfirm={() => void remove()} />}
    <div className="management-card"><span className="panel-kicker">Manufacturer · Catalog</span><h2 id="brand-management-title">Thương hiệu</h2><p className="panel-description">Chuẩn hóa hãng sản xuất để Staff gắn đúng dữ liệu và khách hàng lọc sản phẩm.</p>
      <label className="workspace-search">Tìm thương hiệu<input type="search" value={query} onChange={e => setQuery(e.target.value)} placeholder="Tên, slug hoặc quốc gia" /></label>
      {loading ? <p role="status">Đang tải…</p> : <div className="category-admin-list">{visible.map(x => <article className="category-admin-row" key={x.id}><div><strong>{x.name}</strong><code>/{x.slug}</code><small>{x.country || 'Chưa rõ quốc gia'} · {x.productCount} sản phẩm</small></div><span className={`status-badge ${x.isActive ? 'badge-success' : 'badge-muted'}`}>{x.isActive ? 'Đang dùng' : 'Ngừng dùng'}</span><div className="management-card__actions"><button type="button" className="button-secondary" onClick={() => edit(x)}>Sửa</button><button type="button" className="button-secondary" disabled={busy} onClick={() => void toggle(x)}>{x.isActive ? 'Ngừng dùng' : 'Kích hoạt'}</button><button type="button" className="text-button danger" disabled={busy || x.productCount > 0} onClick={() => setDeleting(x)}>Xóa</button></div></article>)}{visible.length === 0 && <div className="empty-state"><strong>Không có thương hiệu phù hợp</strong></div>}</div>}
    </div>
    <div className="management-card category-editor-card"><span className="panel-kicker">{editing ? 'Chỉnh sửa' : 'Tạo mới'}</span><h2>{editing ? editing.name : 'Thương hiệu mới'}</h2>{message && <div className={`alert ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}>{message.text}</div>}<form className="category-editor-form" onSubmit={submit}>
      <label>Tên thương hiệu<input required minLength={2} maxLength={160} value={name} onChange={e => { setName(e.target.value); if (!slugTouched) setSlug(slugifyBrand(e.target.value)) }} placeholder="Ví dụ: Yonex" /></label>
      <label>Slug<input required pattern="[a-z0-9]+(?:-[a-z0-9]+)*" value={slug} onChange={e => { setSlugTouched(true); setSlug(e.target.value.toLowerCase()) }} /></label>
      <label>Quốc gia<input maxLength={100} value={country} onChange={e => setCountry(e.target.value)} placeholder="Nhật Bản" /></label>
      <label>Website<input type="url" maxLength={500} value={websiteUrl} onChange={e => setWebsiteUrl(e.target.value)} placeholder="https://..." /></label>
      <label className="checkbox-row"><input type="checkbox" checked={isActive} onChange={e => setIsActive(e.target.checked)} />Cho phép Staff sử dụng</label>
      <div className="management-card__actions"><button type="submit" className="button-primary" disabled={busy}>{busy ? 'Đang lưu…' : editing ? 'Lưu thay đổi' : 'Tạo thương hiệu'}</button>{editing && <button type="button" className="button-secondary" onClick={reset}>Hủy</button>}</div>
    </form></div>
  </section>
}
