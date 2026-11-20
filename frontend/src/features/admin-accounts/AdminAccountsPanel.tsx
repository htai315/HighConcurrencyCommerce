import { useEffect, useState } from 'react'
import { useRouteLoaderData } from 'react-router-dom'
import { sessionLoader } from '../auth/auth.api'
import {
  type AccountStatus,
  type AdminAccountListItem,
  type AdminAccountQuery,
  adminAccountErrorMessage,
  getAdminAccounts,
  inviteStaff,
  resendStaffInvitation,
  revokeAccountSessions,
  setAccountStatus,
} from './admin-accounts.api'
import { ConfirmDialog } from '../../components/layout/ConfirmDialog'

export function AdminAccountsPanel() {
  const currentUser = useRouteLoaderData<typeof sessionLoader>('session')

  const [items, setItems] = useState<AdminAccountListItem[]>([])
  const [totalCount, setTotalCount] = useState(0)
  const [page, setPage] = useState(1)
  const pageSize = 15

  const [queryQ, setQueryQ] = useState('')
  const [queryRole, setQueryRole] = useState('')
  const [queryStatus, setQueryStatus] = useState('')
  const [queryLifecycle, setQueryLifecycle] = useState('')

  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [reload, setReload] = useState(0)

  // Invite modal / state
  const [showInviteForm, setShowInviteForm] = useState(false)
  const [inviteEmail, setInviteEmail] = useState('')
  const [inviteFullName, setInviteFullName] = useState('')
  const [inviteSubmitting, setInviteSubmitting] = useState(false)
  const [inviteError, setInviteError] = useState<string | null>(null)

  // Confirmation dialog state
  const [confirmDialog, setConfirmDialog] = useState<{
    title: string
    description: string
    confirmLabel: string
    action: () => Promise<void>
  } | null>(null)

  const [appliedFilters, setAppliedFilters] = useState({
    q: '',
    role: '',
    status: '',
    lifecycle: '',
  })

  useEffect(() => {
    let ignore = false
    const query: AdminAccountQuery = {
      page,
      pageSize,
      q: appliedFilters.q.trim() || undefined,
      role: appliedFilters.role || undefined,
      status: appliedFilters.status || undefined,
      lifecycle: appliedFilters.lifecycle || undefined,
    }
    getAdminAccounts(query)
      .then(res => {
        if (!ignore) {
          setItems(res.items)
          setTotalCount(res.totalCount)
        }
      })
      .catch(err => {
        if (!ignore) {
          setMessage({ type: 'error', text: adminAccountErrorMessage(err) })
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoading(false)
        }
      })
    return () => {
      ignore = true
    }
  }, [page, reload, appliedFilters, pageSize])

  const handleSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setPage(1)
    setAppliedFilters({
      q: queryQ,
      role: queryRole,
      status: queryStatus,
      lifecycle: queryLifecycle,
    })
  }

  const handleResetFilters = () => {
    setQueryQ('')
    setQueryRole('')
    setQueryStatus('')
    setQueryLifecycle('')
    setLoading(true)
    setPage(1)
    setAppliedFilters({
      q: '',
      role: '',
      status: '',
      lifecycle: '',
    })
  }

  const refreshAccounts = () => {
    setReload(r => r + 1)
  }

  const handleInviteSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setInviteSubmitting(true)
    setInviteError(null)
    setMessage(null)

    try {
      const created = await inviteStaff({
        email: inviteEmail.trim(),
        fullName: inviteFullName.trim(),
      })
      setMessage({
        type: 'success',
        text: `Đã gửi lời mời tham gia nhân sự tới email: ${created.email}. Đã lưu Audit Log.`,
      })
      setInviteEmail('')
      setInviteFullName('')
      setShowInviteForm(false)
      refreshAccounts()
    } catch (err) {
      setInviteError(adminAccountErrorMessage(err))
    } finally {
      setInviteSubmitting(false)
    }
  }

  const handleResendInvite = (account: AdminAccountListItem) => {
    setConfirmDialog({
      title: 'Gửi lại lời mời tham gia?',
      description: `Hệ thống sẽ vô hiệu hóa liên kết cũ và gửi email kích hoạt mới kèm token 24 giờ tới ${account.email}.`,
      confirmLabel: 'Gửi lại lời mời',
      action: async () => {
        setBusy(true)
        try {
          await resendStaffInvitation(account.id)
          setMessage({
            type: 'success',
            text: `Đã gửi lại lời mời kích hoạt tới ${account.email}. Đã lưu Audit Log.`,
          })
          refreshAccounts()
        } catch (err) {
          setMessage({ type: 'error', text: adminAccountErrorMessage(err) })
        } finally {
          setBusy(false)
          setConfirmDialog(null)
        }
      },
    })
  }

  const handleToggleStatus = (account: AdminAccountListItem) => {
    const nextStatus: AccountStatus = account.accountStatus === 'ACTIVE' ? 'LOCKED' : 'ACTIVE'
    const isLocking = nextStatus === 'LOCKED'

    setConfirmDialog({
      title: isLocking ? 'Khóa tài khoản này?' : 'Mở khóa tài khoản?',
      description: isLocking
        ? `Tài khoản ${account.email} sẽ bị khóa ngay lập tức và toàn bộ phiên đăng nhập hiện tại sẽ bị thu hồi.`
        : `Tài khoản ${account.email} sẽ được khôi phục trạng thái hoạt động bình thường.`,
      confirmLabel: isLocking ? 'Xác nhận khóa' : 'Mở khóa',
      action: async () => {
        setBusy(true)
        try {
          await setAccountStatus(account.id, nextStatus)
          setMessage({
            type: 'success',
            text: `Đã ${isLocking ? 'khóa' : 'mở khóa'} tài khoản ${account.email}. Đã lưu Audit Log.`,
          })
          refreshAccounts()
        } catch (err) {
          setMessage({ type: 'error', text: adminAccountErrorMessage(err) })
        } finally {
          setBusy(false)
          setConfirmDialog(null)
        }
      },
    })
  }

  const handleRevokeSessions = (account: AdminAccountListItem) => {
    setConfirmDialog({
      title: 'Thu hồi toàn bộ phiên đăng nhập?',
      description: `Tất cả phiên đăng nhập đang hoạt động của ${account.email} (${account.activeSessionCount} phiên) sẽ bị xóa ngay lập tức. Người dùng sẽ bị đăng xuất khỏi tất cả thiết bị.`,
      confirmLabel: 'Thu hồi toàn bộ phiên',
      action: async () => {
        setBusy(true)
        try {
          await revokeAccountSessions(account.id)
          setMessage({
            type: 'success',
            text: `Đã thu hồi toàn bộ phiên đăng nhập của ${account.email}. Đã lưu Audit Log.`,
          })
          refreshAccounts()
        } catch (err) {
          setMessage({ type: 'error', text: adminAccountErrorMessage(err) })
        } finally {
          setBusy(false)
          setConfirmDialog(null)
        }
      },
    })
  }

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))

  return (
    <div className="management-card admin-accounts-panel">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 16, marginBottom: 20 }}>
        <div>
          <h2 style={{ margin: 0, fontSize: 20 }}>Tài khoản &amp; Nhân sự ({totalCount})</h2>
          <p style={{ color: 'var(--muted)', margin: '4px 0 0', fontSize: 13 }}>
            Quản trị danh sách người dùng, mời nhân sự Staff và kiểm soát phiên hoạt động an toàn.
          </p>
        </div>
        <button
          type="button"
          className="button-primary"
          data-testid="admin-invite-staff-toggle-button"
          onClick={() => {
            setShowInviteForm(!showInviteForm)
            setInviteError(null)
          }}
          disabled={busy || loading}
        >
          {showInviteForm ? '✕ Đóng form mời' : '＋ Mời nhân sự (Staff)'}
        </button>
      </div>

      {message && (
        <div
          role="alert"
          className={`alert ${message.type === 'success' ? 'alert-success' : 'alert-error'}`}
          style={{ marginBottom: 16 }}
        >
          {message.text}
        </div>
      )}

      {showInviteForm && (
        <form
          onSubmit={handleInviteSubmit}
          className="management-card"
          style={{ background: 'var(--bg-subtle, #f9fafb)', border: '1px solid var(--border)', borderRadius: 8, padding: 16, marginBottom: 20 }}
        >
          <h3 style={{ margin: '0 0 12px', fontSize: 16 }}>Mời nhân sự mới</h3>
          <p style={{ margin: '0 0 16px', color: 'var(--muted)', fontSize: 13 }}>
            Nhân sự sẽ nhận email chứa liên kết token dùng 1 lần (hiệu lực 24h) để tự đặt mật khẩu và kích hoạt tài khoản. Hệ thống không tạo mật khẩu mặc định.
          </p>

          {inviteError && (
            <div role="alert" className="alert alert-error" style={{ marginBottom: 12 }}>
              {inviteError}
            </div>
          )}

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: 12, marginBottom: 16 }}>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: 13, fontWeight: 500 }}>
              Địa chỉ email Staff *
              <input
                type="email"
                name="inviteEmail"
                data-testid="admin-invite-staff-email-input"
                required
                maxLength={320}
                placeholder="staff@example.com"
                value={inviteEmail}
                onChange={e => setInviteEmail(e.target.value)}
                disabled={inviteSubmitting}
                className="input"
              />
            </label>

            <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: 13, fontWeight: 500 }}>
              Họ và tên *
              <input
                type="text"
                name="inviteFullName"
                data-testid="admin-invite-staff-name-input"
                required
                minLength={2}
                maxLength={100}
                placeholder="Nguyễn Văn A"
                value={inviteFullName}
                onChange={e => setInviteFullName(e.target.value)}
                disabled={inviteSubmitting}
                className="input"
              />
            </label>
          </div>

          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            <button
              type="button"
              className="button-secondary"
              onClick={() => setShowInviteForm(false)}
              disabled={inviteSubmitting}
            >
              Hủy
            </button>
            <button
              type="submit"
              className="button-primary"
              data-testid="admin-invite-staff-submit-button"
              disabled={inviteSubmitting || !inviteEmail.trim() || !inviteFullName.trim()}
            >
              {inviteSubmitting ? 'Đang gửi lời mời…' : 'Gửi lời mời Staff'}
            </button>
          </div>
        </form>
      )}

      {/* Filter toolbar */}
      <form onSubmit={handleSearchSubmit} style={{ display: 'flex', flexWrap: 'wrap', gap: 12, alignItems: 'flex-end', marginBottom: 16 }}>
        <label style={{ flex: '1 1 200px', display: 'flex', flexDirection: 'column', gap: 4, fontSize: 12, fontWeight: 500 }}>
          Tìm kiếm
          <input
            type="search"
            placeholder="Tìm theo email, họ tên…"
            value={queryQ}
            onChange={e => setQueryQ(e.target.value)}
            disabled={loading || busy}
            className="input"
          />
        </label>

        <label style={{ width: 140, display: 'flex', flexDirection: 'column', gap: 4, fontSize: 12, fontWeight: 500 }}>
          Vai trò
          <select
            value={queryRole}
            onChange={e => setQueryRole(e.target.value)}
            disabled={loading || busy}
            className="input"
          >
            <option value="">Tất cả vai trò</option>
            <option value="CUSTOMER">Customer</option>
            <option value="STAFF">Staff</option>
            <option value="ADMIN">Admin</option>
          </select>
        </label>

        <label style={{ width: 140, display: 'flex', flexDirection: 'column', gap: 4, fontSize: 12, fontWeight: 500 }}>
          Trạng thái
          <select
            value={queryStatus}
            onChange={e => setQueryStatus(e.target.value)}
            disabled={loading || busy}
            className="input"
          >
            <option value="">Tất cả trạng thái</option>
            <option value="ACTIVE">ACTIVE</option>
            <option value="LOCKED">LOCKED</option>
          </select>
        </label>

        <label style={{ width: 160, display: 'flex', flexDirection: 'column', gap: 4, fontSize: 12, fontWeight: 500 }}>
          Vòng đời
          <select
            value={queryLifecycle}
            onChange={e => setQueryLifecycle(e.target.value)}
            disabled={loading || busy}
            className="input"
          >
            <option value="">Tất cả vòng đời</option>
            <option value="INVITATION_PENDING">Chờ kích hoạt</option>
            <option value="ACTIVE">Đang hoạt động</option>
            <option value="LOCKED">Đã khóa</option>
          </select>
        </label>

        <div style={{ display: 'flex', gap: 8 }}>
          <button type="submit" className="button-secondary" disabled={loading || busy}>
            Lọc
          </button>
          <button type="button" className="button-secondary" onClick={handleResetFilters} disabled={loading || busy}>
            Đặt lại
          </button>
        </div>
      </form>

      {/* Table */}
      <div className="table-responsive">
        <table className="admin-table">
          <thead>
            <tr>
              <th>Tài khoản</th>
              <th>Vai trò</th>
              <th>Vòng đời</th>
              <th>Xác minh</th>
              <th style={{ textAlign: 'center' }}>Phiên hoạt động</th>
              <th>Ngày tạo</th>
              <th style={{ textAlign: 'right' }}>Thao tác</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr>
                <td colSpan={7} style={{ textAlign: 'center', padding: 32, color: 'var(--muted)' }}>
                  Đang tải danh sách tài khoản…
                </td>
              </tr>
            ) : items.length === 0 ? (
              <tr>
                <td colSpan={7} style={{ textAlign: 'center', padding: 32, color: 'var(--muted)' }}>
                  Không tìm thấy tài khoản nào phù hợp với bộ lọc.
                </td>
              </tr>
            ) : (
              items.map(account => {
                const isSelf = currentUser?.id === account.id
                return (
                  <tr key={account.id}>
                    <td>
                      <strong>{account.fullName}</strong>
                      <div style={{ color: 'var(--muted)', fontSize: 12 }}>{account.email}</div>
                    </td>
                    <td>
                      <span className={`badge badge-${account.role.toLowerCase()}`}>
                        {account.role}
                      </span>
                    </td>
                    <td>
                      {account.lifecycleStatus === 'INVITATION_PENDING' ? (
                        <span className="badge badge-warning" style={{ background: '#fef3c7', color: '#92400e' }}>
                          Chờ kích hoạt
                        </span>
                      ) : account.lifecycleStatus === 'LOCKED' ? (
                        <span className="badge badge-danger" style={{ background: '#fee2e2', color: '#991b1b' }}>
                          Đã khóa
                        </span>
                      ) : (
                        <span className="badge badge-success" style={{ background: '#dcfce7', color: '#166534' }}>
                          Hoạt động
                        </span>
                      )}
                    </td>
                    <td>
                      {account.emailVerifiedAt ? (
                        <span style={{ color: '#16a34a', fontSize: 12 }}>✓ Đã xác minh</span>
                      ) : (
                        <span style={{ color: '#d97706', fontSize: 12 }}>Chưa xác minh</span>
                      )}
                    </td>
                    <td style={{ textAlign: 'center' }}>
                      <span style={{ fontWeight: 600 }}>{account.activeSessionCount}</span>
                      {account.latestSessionAt && (
                        <div style={{ color: 'var(--muted)', fontSize: 11 }}>
                          Gần nhất: {new Date(account.latestSessionAt).toLocaleTimeString('vi-VN')}
                        </div>
                      )}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--muted)' }}>
                      {new Date(account.createdAt).toLocaleDateString('vi-VN')}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {isSelf ? (
                        <span style={{ fontSize: 12, color: 'var(--muted)', fontStyle: 'italic' }}>
                          (Tài khoản của bạn)
                        </span>
                      ) : (
                        <div style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                          {account.role === 'STAFF' && account.lifecycleStatus === 'INVITATION_PENDING' && (
                            <button
                              type="button"
                              className="button-secondary"
                              style={{ minHeight: 30, padding: '0 8px', fontSize: 11 }}
                              disabled={busy || loading}
                              onClick={() => handleResendInvite(account)}
                            >
                              Gửi lại mời
                            </button>
                          )}

                          <button
                            type="button"
                            className="button-secondary"
                            style={{ minHeight: 30, padding: '0 8px', fontSize: 11 }}
                            disabled={busy || loading}
                            onClick={() => handleToggleStatus(account)}
                          >
                            {account.accountStatus === 'ACTIVE' ? 'Khóa' : 'Mở khóa'}
                          </button>

                          {account.activeSessionCount > 0 && (
                            <button
                              type="button"
                              className="button-secondary"
                              style={{ minHeight: 30, padding: '0 8px', fontSize: 11, color: '#dc2626' }}
                              disabled={busy || loading}
                              onClick={() => handleRevokeSessions(account)}
                            >
                              Thu hồi phiên
                            </button>
                          )}
                        </div>
                      )}
                    </td>
                  </tr>
                )
              })
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 16 }}>
          <span style={{ fontSize: 13, color: 'var(--muted)' }}>
            Trang {page} / {totalPages} (Tổng cộng {totalCount} tài khoản)
          </span>
          <div style={{ display: 'flex', gap: 8 }}>
            <button
              type="button"
              className="button-secondary"
              disabled={page <= 1 || loading || busy}
              onClick={() => {
                const prev = page - 1
                setLoading(true)
                setPage(prev)
              }}
            >
              ← Trước
            </button>
            <button
              type="button"
              className="button-secondary"
              disabled={page >= totalPages || loading || busy}
              onClick={() => {
                const next = page + 1
                setLoading(true)
                setPage(next)
              }}
            >
              Sau →
            </button>
          </div>
        </div>
      )}

      {/* Confirm dialog */}
      {confirmDialog && (
        <ConfirmDialog
          title={confirmDialog.title}
          description={confirmDialog.description}
          confirmLabel={confirmDialog.confirmLabel}
          busy={busy}
          onConfirm={() => void confirmDialog.action()}
          onCancel={() => setConfirmDialog(null)}
        />
      )}
    </div>
  )
}
