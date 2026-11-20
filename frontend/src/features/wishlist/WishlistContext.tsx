import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import type { AuthUser } from '../auth/auth.api'
import {
  addToWishlist,
  getWishlistAlertSettings,
  getWishlistSummary,
  removeFromWishlist,
  updateWishlistAlertSettings,
} from './wishlist.api'
import type { UpdateWishlistAlertSettings, WishlistAlertSettings, WishlistAlertSummary } from './wishlist.api'
import { ApiError } from '../../lib/api/client'
import { WishlistContext } from './wishlist-context'

const EMPTY_IDS = new Set<string>()
const EMPTY_ALERT_SUMMARIES = new Map<string, WishlistAlertSummary>()

type WishlistState = {
  ownerId: string | null
  ids: Set<string>
  totalCount: number
  alerts: Map<string, WishlistAlertSummary>
}

export function WishlistProvider({
  user,
  children,
}: {
  user?: AuthUser | null
  children: ReactNode
}) {
  const navigate = useNavigate()
  const customerId = user?.role === 'CUSTOMER' ? user.id : null
  const activeCustomerIdRef = useRef(customerId)
  const [state, setState] = useState<WishlistState>({ ownerId: null, ids: EMPTY_IDS, totalCount: 0, alerts: new Map() })
  const [loadingOwnerId, setLoadingOwnerId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const requestGenerationRef = useRef(0)
  const summaryAbortRef = useRef<AbortController | null>(null)
  const pendingMutationsRef = useRef(new Map<string, Promise<boolean>>())

  useEffect(() => {
    activeCustomerIdRef.current = customerId
  }, [customerId])

  const invalidateSummaryRequest = useCallback(() => {
    requestGenerationRef.current += 1
    summaryAbortRef.current?.abort()
    summaryAbortRef.current = null
  }, [])

  const refreshSummary = useCallback(async () => {
    const requestOwnerId = customerId
    invalidateSummaryRequest()
    if (!requestOwnerId) {
      setState({ ownerId: null, ids: EMPTY_IDS, totalCount: 0, alerts: new Map() })
      setLoadingOwnerId(null)
      return
    }

    const generation = requestGenerationRef.current
    const controller = new AbortController()
    summaryAbortRef.current = controller
    try {
      setLoadingOwnerId(requestOwnerId)
      const summary = await getWishlistSummary(controller.signal)
      if (controller.signal.aborted || generation !== requestGenerationRef.current || activeCustomerIdRef.current !== requestOwnerId) return
      setState({
        ownerId: requestOwnerId,
        ids: new Set(summary.productIds),
        totalCount: summary.totalCount,
        alerts: new Map(summary.alerts.map(alert => [alert.productId, alert])),
      })
    } catch (err: unknown) {
      if (!(err instanceof DOMException && err.name === 'AbortError') && !controller.signal.aborted) {
        // Keep the last confirmed state when a background refresh fails.
      }
    } finally {
      if (generation === requestGenerationRef.current && activeCustomerIdRef.current === requestOwnerId) setLoadingOwnerId(null)
      if (summaryAbortRef.current === controller) summaryAbortRef.current = null
    }
  }, [customerId, invalidateSummaryRequest])

  useEffect(() => {
    queueMicrotask(() => void refreshSummary())
    return () => invalidateSummaryRequest()
  }, [refreshSummary, invalidateSummaryRequest])

  useEffect(() => {
    const handleChanged = () => void refreshSummary()
    const handleFocus = () => void refreshSummary()
    window.addEventListener('hcc:wishlist-changed', handleChanged)
    window.addEventListener('focus', handleFocus)
    return () => {
      window.removeEventListener('hcc:wishlist-changed', handleChanged)
      window.removeEventListener('focus', handleFocus)
    }
  }, [refreshSummary])

  const wishlistIds = state.ownerId === customerId ? state.ids : EMPTY_IDS
  const totalCount = state.ownerId === customerId ? state.totalCount : 0
  const alertSummaries = state.ownerId === customerId ? state.alerts : EMPTY_ALERT_SUMMARIES
  const loading = customerId !== null && loadingOwnerId === customerId

  const isSaved = useCallback(
    (productId: string) => wishlistIds.has(productId),
    [wishlistIds]
  )

  const getAlertSummary = useCallback(
    (productId: string) => alertSummaries.get(productId),
    [alertSummaries]
  )

  const commitAlertSettings = useCallback((ownerId: string, settings: WishlistAlertSettings) => {
    if (activeCustomerIdRef.current !== ownerId) return
    setState(previous => {
      if (previous.ownerId !== ownerId || !previous.ids.has(settings.productId)) return previous
      const alerts = new Map(previous.alerts)
      alerts.set(settings.productId, settings)
      return { ...previous, alerts }
    })
  }, [])

  const loadAlertSettings = useCallback(async (productId: string, signal?: AbortSignal) => {
    if (!customerId) throw new ApiError(403, { code: 'FORBIDDEN' })
    const ownerId = customerId
    const settings = await getWishlistAlertSettings(productId, signal)
    commitAlertSettings(ownerId, settings)
    return settings
  }, [customerId, commitAlertSettings])

  const saveAlertSettings = useCallback(async (
    productId: string,
    request: UpdateWishlistAlertSettings,
    signal?: AbortSignal,
  ) => {
    if (!customerId) throw new ApiError(403, { code: 'FORBIDDEN' })
    const ownerId = customerId
    const settings = await updateWishlistAlertSettings(productId, request, signal)
    commitAlertSettings(ownerId, settings)
    return settings
  }, [customerId, commitAlertSettings])

  const clearError = useCallback(() => setError(null), [])

  const toggleWishlist = useCallback(
    async (productId: string): Promise<boolean> => {
      setError(null)
      if (!customerId) {
        if (!user) {
          const returnTo = `/products/${encodeURIComponent(productId)}`
          navigate(`/login?returnTo=${encodeURIComponent(returnTo)}`)
        } else setError('Chỉ tài khoản khách hàng mới có thể lưu sản phẩm yêu thích.')
        return false
      }

      const existingMutation = pendingMutationsRef.current.get(productId)
      if (existingMutation) return existingMutation

      const operation = (async () => {
        invalidateSummaryRequest()
        const currentlySaved = wishlistIds.has(productId)
        setState(previous => {
          const baseIds = previous.ownerId === customerId ? previous.ids : EMPTY_IDS
          const baseCount = previous.ownerId === customerId ? previous.totalCount : 0
          const nextIds = new Set(baseIds)
          const nextAlerts = new Map(previous.ownerId === customerId ? previous.alerts : [])
          if (currentlySaved) nextIds.delete(productId)
          else {
            nextIds.add(productId)
            nextAlerts.set(productId, { productId, backInStockEnabled: false, priceDropEnabled: false, emailEnabled: false, revision: 0 })
          }
          if (currentlySaved) nextAlerts.delete(productId)
          return { ownerId: customerId, ids: nextIds, totalCount: currentlySaved ? Math.max(0, baseCount - 1) : baseCount + 1, alerts: nextAlerts }
        })

        try {
          if (currentlySaved) {
            await removeFromWishlist(productId)
            return false
          }
          await addToWishlist(productId)
          return true
        } catch (err: unknown) {
          void refreshSummary()
          if (err instanceof ApiError && err.code === 'WISHLIST_LIMIT_REACHED') {
            setError('Danh sách yêu thích đã đạt tối đa 500 sản phẩm.')
          } else if (err instanceof ApiError && err.code === 'PRODUCT_NOT_FOUND') {
            setError('Sản phẩm hiện không còn mở bán.')
          } else {
            setError('Thao tác không thành công. Vui lòng thử lại.')
          }
          throw err
        }
      })()

      pendingMutationsRef.current.set(productId, operation)
      try {
        return await operation
      } finally {
        if (pendingMutationsRef.current.get(productId) === operation) {
          pendingMutationsRef.current.delete(productId)
        }
      }
    },
    [customerId, user, wishlistIds, navigate, invalidateSummaryRequest, refreshSummary]
  )

  const value = useMemo(
    () => ({
      wishlistIds,
      totalCount,
      emailVerified: user?.role === 'CUSTOMER' && user.emailVerified,
      alertSummaries,
      loading,
      error,
      isSaved,
      getAlertSummary,
      loadAlertSettings,
      saveAlertSettings,
      toggleWishlist,
      refreshSummary,
      clearError,
    }),
    [wishlistIds, totalCount, alertSummaries, loading, error, user, isSaved, getAlertSummary, loadAlertSettings, saveAlertSettings, toggleWishlist, refreshSummary, clearError]
  )

  return (
    <WishlistContext.Provider value={value}>
      {children}
    </WishlistContext.Provider>
  )
}
