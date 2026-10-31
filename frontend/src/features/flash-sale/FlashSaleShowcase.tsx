import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { formatPrice } from '../catalog/catalog.api'
import { getPublicFlashSales, type PublicFlashSaleCampaign } from './flash-sale.api'

export function FlashSaleShowcase() {
  const [campaigns, setCampaigns] = useState<PublicFlashSaleCampaign[]>([])
  const [loading, setLoading] = useState(true)
  const [timeLeft, setTimeLeft] = useState<{ hours: number; minutes: number; seconds: number } | null>(null)

  useEffect(() => {
    let ignore = false
    getPublicFlashSales()
      .then(data => {
        if (!ignore) {
          const active = data.filter(c => c.campaignStatus === 'ACTIVE' && c.offers.length > 0)
          setCampaigns(active)
        }
      })
      .catch(() => {
        // Silently catch in storefront if no active flash sales or error
      })
      .finally(() => {
        if (!ignore) setLoading(false)
      })

    return () => {
      ignore = true
    }
  }, [])

  const activeCampaign = campaigns[0]

  useEffect(() => {
    if (!activeCampaign) return

    function calculateTime() {
      const end = new Date(activeCampaign?.endsAt ?? '').getTime()
      const now = Date.now()
      const diff = Math.max(0, end - now)

      if (diff <= 0) {
        setTimeLeft({ hours: 0, minutes: 0, seconds: 0 })
        return
      }

      const hours = Math.floor(diff / (1000 * 60 * 60))
      const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60))
      const seconds = Math.floor((diff % (1000 * 60)) / 1000)
      setTimeLeft({ hours, minutes, seconds })
    }

    calculateTime()
    const interval = setInterval(calculateTime, 1000)
    return () => clearInterval(interval)
  }, [activeCampaign])

  if (loading || !activeCampaign || activeCampaign.offers.length === 0) {
    return null
  }

  const format2Digits = (n: number) => String(n).padStart(2, '0')

  return (
    <section className="flash-sale-banner" aria-label="Khuyến mãi Flash Sale giờ vàng">
      <div className="flash-sale-header">
        <div className="flash-sale-title-wrap">
          <span className="flash-icon" aria-hidden="true">⚡</span>
          <div>
            <h2>{activeCampaign.name}</h2>
            <p style={{ margin: 0, fontSize: 13, color: '#fef08a' }}>Giờ vàng giá sốc — Số lượng có hạn</p>
          </div>
        </div>

        {timeLeft && (
          <div className="flash-countdown" role="timer" aria-label="Thời gian còn lại">
            <span>Kết thúc sau:</span>
            <strong>
              {format2Digits(timeLeft.hours)}:{format2Digits(timeLeft.minutes)}:{format2Digits(timeLeft.seconds)}
            </strong>
          </div>
        )}
      </div>

      <div className="flash-offers-grid">
        {activeCampaign.offers.map(offer => {
          const discount = offer.discountPercentage > 0
            ? Math.round(offer.discountPercentage)
            : Math.round(((offer.regularPrice - offer.salePrice) / offer.regularPrice) * 100)

          return (
            <article key={offer.offerId} className="flash-offer-card">
              <span className="flash-badge-discount">-{discount}% GIẢM</span>
              <h3 style={{ fontSize: 16, margin: '4px 0 8px', fontWeight: 700, flex: 1 }}>
                <Link to={`/products/${offer.productId}?flashSaleOfferId=${encodeURIComponent(offer.offerId)}`} style={{ color: 'var(--ink)' }}>
                  {offer.productTitle}
                </Link>
              </h3>
              <p style={{ fontSize: 12, color: 'var(--muted)', margin: '0 0 8px' }}>
                {offer.optionLabel} · SKU: {offer.skuCode}
              </p>

              <div className="flash-price-row">
                <span className="flash-price-active">{formatPrice(offer.salePrice)}</span>
                <span style={{ fontSize: 13, textDecoration: 'line-through', color: 'var(--muted)' }}>
                  {formatPrice(offer.regularPrice)}
                </span>
              </div>

              <div className="flash-quota-progress">
                <div
                  className="flash-quota-bar"
                  style={{ width: `${offer.quotaTotal > 0 ? Math.min(100, Math.max(0, (offer.quotaTotal - offer.remainingQuota) / offer.quotaTotal * 100)) : 0}%` }}
                />
              </div>
              <div className="flash-quota-text">
                <span>Còn lại: {offer.remainingQuota} suất</span>
                <span>Tối đa {offer.perCustomerLimit}/khách</span>
              </div>

              <Link
                to={`/products/${offer.productId}?flashSaleOfferId=${encodeURIComponent(offer.offerId)}`}
                className="button-primary"
                style={{
                  marginTop: 14,
                  minHeight: 38,
                  fontSize: 13,
                  background: 'linear-gradient(135deg, #ea580c 0%, #f97316 100%)',
                  boxShadow: 'var(--shadow-flash)',
                }}
              >
                Săn Deal Ngay
              </Link>
            </article>
          )
        })}
      </div>
    </section>
  )
}

