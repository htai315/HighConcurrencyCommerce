import { useState } from 'react'
import { Form, useActionData, useLoaderData, useNavigation } from 'react-router-dom'
import type { accountAction, accountLoader } from '../features/account/account.api'
import { PageHeading } from '../components/layout/PageHeading'

export function AccountPage() {
  const { profile, addresses } = useLoaderData<typeof accountLoader>()
  const actionData = useActionData<typeof accountAction>()
  const navigation = useNavigation()
  const isSubmitting = navigation.state !== 'idle'

  const [editingAddressId, setEditingAddressId] = useState<string | null>(null)
  const [prevActionData, setPrevActionData] = useState(actionData)

  if (prevActionData !== actionData) {
    setPrevActionData(actionData)
    if (actionData?.success) {
      setEditingAddressId(null)
    }
  }

  return (
    <section className="content-page account-page">
      <PageHeading title="Hồ sơ & Sổ địa chỉ" description="Thông tin của bạn, sẵn sàng cho mỗi lần mua sắm." />

      {actionData?.error && (
        <div className="error-banner" role="alert" style={{ marginBottom: 24, padding: 16, background: '#fdf2f2', border: '1px solid #f8b4b4', borderRadius: 12, color: '#9b1c1c' }}>
          {actionData.error}
        </div>
      )}

      {actionData?.success && (
        <div role="status" style={{ marginBottom: 24, padding: 16, background: '#f0fdf4', border: '1px solid #bbf7d0', borderRadius: 12, color: '#166534' }}>
          {actionData.message}
        </div>
      )}

      <div className="account-layout">
        {/* Phần 1: Hồ sơ cá nhân */}
        <div className="info-panel">
          <span className="panel-kicker">Thông tin cá nhân</span>
          <h2>Hồ sơ của bạn</h2>
          <p>Cập nhật họ tên và số điện thoại dùng khi mua sắm.</p>

          <Form method="post" style={{ marginTop: 20 }}>
            <input type="hidden" name="intent" value="update-profile" />

            <div className="form-field">
              <label htmlFor="email">Email</label>
              <input id="email" type="email" value={profile.email} disabled style={{ background: 'var(--paper-deep)', color: 'var(--muted)' }} />
            </div>

            <div className="form-field">
              <label htmlFor="fullName">Họ và tên *</label>
              <input id="fullName" name="fullName" type="text" defaultValue={profile.fullName} required maxLength={160} />
            </div>

            <div className="form-field">
              <label htmlFor="phone">Số điện thoại</label>
              <input id="phone" name="phone" type="tel" defaultValue={profile.phone ?? ''} placeholder="0901234567" maxLength={16} />
            </div>

            <button type="submit" className="button-primary" disabled={isSubmitting} style={{ marginTop: 12 }}>
              {isSubmitting ? 'Đang lưu…' : 'Lưu hồ sơ'}
            </button>
          </Form>
        </div>

        {/* Phần 2: Sổ địa chỉ */}
        <div style={{ display: 'grid', gap: 24 }}>
          <div className="info-panel">
            <span className="panel-kicker">Sổ địa chỉ</span>
            <h2>Địa chỉ nhận hàng</h2>
            <p>Danh sách địa chỉ dùng để giao hàng khi đặt đơn.</p>

            {addresses.length === 0 ? (
              <p style={{ marginTop: 16, fontStyle: 'italic', color: 'var(--muted)' }}>
                Bạn chưa có địa chỉ giao hàng nào. Hãy thêm địa chỉ đầu tiên bên dưới.
              </p>
            ) : (
              <div style={{ display: 'grid', gap: 16, marginTop: 20 }}>
                {addresses.map((addr) => (
                  <div
                    key={addr.id}
                    className="address-card"
                    style={{
                      border: `1px solid ${addr.isDefault ? 'var(--brand)' : 'var(--line)'}`,
                      borderRadius: 14,
                      padding: 18,
                      background: addr.isDefault ? '#f7faff' : 'var(--paper)',
                      display: 'grid',
                      gap: 8,
                    }}
                  >
                    {editingAddressId === addr.id ? (
                      /* Form sửa địa chỉ */
                      <Form method="post" style={{ display: 'grid', gap: 12 }}>
                        <input type="hidden" name="intent" value="update-address" />
                        <input type="hidden" name="id" value={addr.id} />

                        <div className="form-row">
                          <div className="form-field">
                            <label htmlFor={`edit-name-${addr.id}`}>Tên người nhận *</label>
                            <input id={`edit-name-${addr.id}`} name="recipientName" type="text" defaultValue={addr.recipientName} required maxLength={160} />
                          </div>

                          <div className="form-field">
                            <label htmlFor={`edit-phone-${addr.id}`}>Số điện thoại *</label>
                            <input id={`edit-phone-${addr.id}`} name="phone" type="tel" defaultValue={addr.phone} required maxLength={16} />
                          </div>
                        </div>

                        <div className="form-field">
                          <label htmlFor={`edit-line-${addr.id}`}>Địa chỉ chi tiết *</label>
                          <input id={`edit-line-${addr.id}`} name="addressLine" type="text" defaultValue={addr.addressLine} required maxLength={500} />
                        </div>

                        <div className="form-row">
                          <div className="form-field">
                            <label htmlFor={`edit-ward-${addr.id}`}>Phường / Xã</label>
                            <input id={`edit-ward-${addr.id}`} name="ward" type="text" defaultValue={addr.ward ?? ''} maxLength={160} />
                          </div>

                          <div className="form-field">
                            <label htmlFor={`edit-prov-${addr.id}`}>Tỉnh / Thành phố *</label>
                            <input id={`edit-prov-${addr.id}`} name="province" type="text" defaultValue={addr.province} required maxLength={160} />
                          </div>
                        </div>

                        <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '8px 0' }}>
                          <input id={`edit-default-${addr.id}`} name="isDefault" type="checkbox" value="true" defaultChecked={addr.isDefault} />
                          <label htmlFor={`edit-default-${addr.id}`} style={{ fontSize: 14, cursor: 'pointer' }}>
                            Đặt làm địa chỉ giao hàng mặc định
                          </label>
                        </div>

                        <div style={{ display: 'flex', gap: 12, marginTop: 4 }}>
                          <button type="submit" className="button-primary" disabled={isSubmitting} style={{ minHeight: 38, padding: '0 16px', fontSize: 14 }}>
                            {isSubmitting ? 'Đang lưu…' : 'Lưu thay đổi'}
                          </button>
                          <button type="button" className="button-secondary" onClick={() => setEditingAddressId(null)} disabled={isSubmitting} style={{ minHeight: 38, padding: '0 16px', fontSize: 14 }}>
                            Hủy
                          </button>
                        </div>
                      </Form>
                    ) : (
                      /* Chế độ xem địa chỉ */
                      <>
                        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                          <strong style={{ fontSize: 16 }}>{addr.recipientName}</strong>
                          {addr.isDefault ? (
                            <span className="stock-label" style={{ background: '#e0edff', color: 'var(--brand)', padding: '2px 8px', borderRadius: 6 }}>
                              Mặc định
                            </span>
                          ) : null}
                        </div>

                        <div style={{ color: 'var(--muted)', fontSize: 14 }}>
                          <span>SĐT: {addr.phone}</span>
                          <br />
                          <span>{addr.addressLine}{addr.ward ? `, ${addr.ward}` : ''}, {addr.province}</span>
                        </div>

                        <div style={{ display: 'flex', gap: 12, marginTop: 8, alignItems: 'center' }}>
                          <button
                            type="button"
                            className="button-secondary"
                            onClick={() => setEditingAddressId(addr.id)}
                            disabled={isSubmitting}
                            style={{ minHeight: 34, fontSize: 13, padding: '0 12px' }}
                          >
                            Sửa
                          </button>

                          {!addr.isDefault && (
                            <Form method="post">
                              <input type="hidden" name="intent" value="update-address" />
                              <input type="hidden" name="id" value={addr.id} />
                              <input type="hidden" name="recipientName" value={addr.recipientName} />
                              <input type="hidden" name="phone" value={addr.phone} />
                              <input type="hidden" name="addressLine" value={addr.addressLine} />
                              <input type="hidden" name="ward" value={addr.ward ?? ''} />
                              <input type="hidden" name="province" value={addr.province} />
                              <input type="hidden" name="isDefault" value="true" />
                              <button type="submit" className="button-secondary" disabled={isSubmitting} style={{ minHeight: 34, fontSize: 13, padding: '0 12px' }}>
                                Đặt làm mặc định
                              </button>
                            </Form>
                          )}

                          <Form
                            method="post"
                            onSubmit={(e) => {
                              if (!confirm('Bạn có chắc chắn muốn xóa địa chỉ này?')) {
                                e.preventDefault()
                              }
                            }}
                          >
                            <input type="hidden" name="intent" value="delete-address" />
                            <input type="hidden" name="id" value={addr.id} />
                            <button
                              type="submit"
                              disabled={isSubmitting}
                              style={{
                                minHeight: 34,
                                fontSize: 13,
                                padding: '0 12px',
                                background: 'transparent',
                                border: '1px solid #f8b4b4',
                                borderRadius: 10,
                                color: '#9b1c1c',
                                cursor: 'pointer',
                              }}
                            >
                              Xóa
                            </button>
                          </Form>
                        </div>
                      </>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Form thêm địa chỉ mới */}
          <div className="info-panel">
            <span className="panel-kicker">Địa chỉ mới</span>
            <h3>Thêm địa chỉ giao hàng</h3>

            <Form method="post" style={{ marginTop: 16 }}>
              <input type="hidden" name="intent" value="create-address" />

              <div className="form-row">
                <div className="form-field">
                  <label htmlFor="recipientName">Tên người nhận *</label>
                  <input id="recipientName" name="recipientName" type="text" required maxLength={160} />
                </div>

                <div className="form-field">
                  <label htmlFor="addrPhone">Số điện thoại *</label>
                  <input id="addrPhone" name="phone" type="tel" placeholder="0901234567" required maxLength={16} />
                </div>
              </div>

              <div className="form-field">
                <label htmlFor="addressLine">Địa chỉ chi tiết (số nhà, tên đường) *</label>
                <input id="addressLine" name="addressLine" type="text" placeholder="123 Đường Cầu Lông" required maxLength={500} />
              </div>

              <div className="form-row">
                <div className="form-field">
                  <label htmlFor="ward">Phường / Xã</label>
                  <input id="ward" name="ward" type="text" maxLength={160} />
                </div>

                <div className="form-field">
                  <label htmlFor="province">Tỉnh / Thành phố *</label>
                  <input id="province" name="province" type="text" placeholder="TP. Hồ Chí Minh" required maxLength={160} />
                </div>
              </div>

              <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '14px 0 18px' }}>
                <input id="isDefault" name="isDefault" type="checkbox" value="true" defaultChecked={addresses.length === 0} />
                <label htmlFor="isDefault" style={{ fontSize: 14, cursor: 'pointer' }}>
                  Đặt làm địa chỉ giao hàng mặc định
                </label>
              </div>

              <button type="submit" className="button-primary" disabled={isSubmitting}>
                {isSubmitting ? 'Đang lưu…' : 'Thêm địa chỉ'}
              </button>
            </Form>
          </div>
        </div>
      </div>
    </section>
  )
}
