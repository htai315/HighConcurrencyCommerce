import { ApiError, apiRequest, isRecord } from '../../lib/api/client'

export type RatingDistribution = {
  one: number
  two: number
  three: number
  four: number
  five: number
}

export type ProductRatingSummary = {
  averageRating: number
  reviewCount: number
  distribution: RatingDistribution
}

export type PublicProductReview = {
  id: string
  rating: number
  content: string
  createdAt: string
  updatedAt: string
  reviewerDisplayName: string
  verifiedPurchase: boolean
}

export type ProductReviewPage = {
  items: PublicProductReview[]
  summary: ProductRatingSummary
  page: number
  pageSize: number
  totalCount: number
}

export type MyProductReview = {
  id: string
  productId: string
  productTitle: string
  rating: number
  content: string
  reviewStatus: 'PUBLISHED' | 'HIDDEN'
  createdAt: string
  updatedAt: string
  moderatedAt: string | null
  moderationReason: string | null
}

export type ReviewEligibility = {
  eligible: boolean
  reasonCode: 'NO_DELIVERED_ORDER' | 'ALREADY_REVIEWED' | null
  existingReview: MyProductReview | null
}

export type ModerationProductReview = {
  id: string
  productId: string
  productTitle: string
  customerId: string
  customerName: string
  customerEmail: string
  rating: number
  content: string
  reviewStatus: 'PUBLISHED' | 'HIDDEN'
  createdAt: string
  updatedAt: string
  moderatedBy: string | null
  moderatorName: string | null
  moderatedAt: string | null
  moderationReason: string | null
  deletedAt: string | null
}

export type ModerationProductReviewPage = {
  items: ModerationProductReview[]
  page: number
  pageSize: number
  totalCount: number
}

function invalid(): never {
  throw new ApiError(502, { code: 'INVALID_RESPONSE' })
}

function string(value: unknown): string {
  return typeof value === 'string' ? value : invalid()
}

function nullableString(value: unknown): string | null {
  return value === null || value === undefined ? null : string(value)
}

function number(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : invalid()
}

function integer(value: unknown, minimum = 0): number {
  const parsed = number(value)
  return Number.isSafeInteger(parsed) && parsed >= minimum ? parsed : invalid()
}

function boolean(value: unknown): boolean {
  return typeof value === 'boolean' ? value : invalid()
}

function list<T>(value: unknown, parse: (item: unknown) => T): T[] {
  return Array.isArray(value) ? value.map(parse) : invalid()
}

function parseDistribution(value: unknown): RatingDistribution {
  if (!isRecord(value)) return invalid()
  return {
    one: integer(value.one, 0),
    two: integer(value.two, 0),
    three: integer(value.three, 0),
    four: integer(value.four, 0),
    five: integer(value.five, 0),
  }
}

function parseSummary(value: unknown): ProductRatingSummary {
  if (!isRecord(value)) return invalid()
  return {
    averageRating: number(value.averageRating),
    reviewCount: integer(value.reviewCount, 0),
    distribution: parseDistribution(value.distribution),
  }
}

function parsePublicReview(value: unknown): PublicProductReview {
  if (!isRecord(value)) return invalid()
  return {
    id: string(value.id),
    rating: integer(value.rating, 1),
    content: string(value.content),
    createdAt: string(value.createdAt),
    updatedAt: string(value.updatedAt),
    reviewerDisplayName: string(value.reviewerDisplayName),
    verifiedPurchase: boolean(value.verifiedPurchase),
  }
}

function parsePublicPage(value: unknown): ProductReviewPage {
  if (!isRecord(value)) return invalid()
  return {
    items: list(value.items, parsePublicReview),
    summary: parseSummary(value.summary),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount, 0),
  }
}

function parseMyReview(value: unknown): MyProductReview {
  if (!isRecord(value)) return invalid()
  const status = string(value.reviewStatus)
  if (status !== 'PUBLISHED' && status !== 'HIDDEN') return invalid()
  return {
    id: string(value.id),
    productId: string(value.productId),
    productTitle: string(value.productTitle),
    rating: integer(value.rating, 1),
    content: string(value.content),
    reviewStatus: status,
    createdAt: string(value.createdAt),
    updatedAt: string(value.updatedAt),
    moderatedAt: nullableString(value.moderatedAt),
    moderationReason: nullableString(value.moderationReason),
  }
}

function parseEligibility(value: unknown): ReviewEligibility {
  if (!isRecord(value)) return invalid()
  const reason = value.reasonCode === null || value.reasonCode === undefined ? null : string(value.reasonCode)
  return {
    eligible: boolean(value.eligible),
    reasonCode: reason === 'NO_DELIVERED_ORDER' || reason === 'ALREADY_REVIEWED' ? reason : null,
    existingReview: value.existingReview ? parseMyReview(value.existingReview) : null,
  }
}

function parseModerationReview(value: unknown): ModerationProductReview {
  if (!isRecord(value)) return invalid()
  const status = string(value.reviewStatus)
  if (status !== 'PUBLISHED' && status !== 'HIDDEN') return invalid()
  return {
    id: string(value.id),
    productId: string(value.productId),
    productTitle: string(value.productTitle),
    customerId: string(value.customerId),
    customerName: string(value.customerName),
    customerEmail: string(value.customerEmail),
    rating: integer(value.rating, 1),
    content: string(value.content),
    reviewStatus: status,
    createdAt: string(value.createdAt),
    updatedAt: string(value.updatedAt),
    moderatedBy: nullableString(value.moderatedBy),
    moderatorName: nullableString(value.moderatorName),
    moderatedAt: nullableString(value.moderatedAt),
    moderationReason: nullableString(value.moderationReason),
    deletedAt: nullableString(value.deletedAt),
  }
}

function parseModerationPage(value: unknown): ModerationProductReviewPage {
  if (!isRecord(value)) return invalid()
  return {
    items: list(value.items, parseModerationReview),
    page: integer(value.page, 1),
    pageSize: integer(value.pageSize, 1),
    totalCount: integer(value.totalCount, 0),
  }
}

async function sendMutation(path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: unknown, signal?: AbortSignal): Promise<unknown> {
  const csrf = await apiRequest('/auth/csrf', { signal, cache: 'no-store' })
  if (!isRecord(csrf) || typeof csrf.requestToken !== 'string') throw new ApiError(502, { code: 'INVALID_RESPONSE' })
  return apiRequest(path, {
    method,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
    headers: { 'X-CSRF-TOKEN': csrf.requestToken, 'Content-Type': 'application/json' },
  })
}

export async function getPublicReviews(
  productId: string,
  query?: { sort?: 'newest' | 'highest' | 'lowest'; rating?: number; page?: number; pageSize?: number },
  signal?: AbortSignal
): Promise<ProductReviewPage> {
  const params = new URLSearchParams()
  if (query?.sort) params.set('sort', query.sort)
  if (typeof query?.rating === 'number' && query.rating >= 1 && query.rating <= 5) params.set('rating', String(query.rating))
  if (query?.page) params.set('page', String(query.page))
  if (query?.pageSize) params.set('pageSize', String(query.pageSize))
  const qs = params.toString()
  const data = await apiRequest(`/catalog/products/${encodeURIComponent(productId)}/reviews${qs ? `?${qs}` : ''}`, { signal })
  return parsePublicPage(data)
}

export async function getReviewEligibility(productId: string, signal?: AbortSignal): Promise<ReviewEligibility> {
  const data = await apiRequest(`/products/${encodeURIComponent(productId)}/review-eligibility`, { signal, cache: 'no-store' })
  return parseEligibility(data)
}

export async function createReview(
  productId: string,
  data: { rating: number; content: string },
  signal?: AbortSignal
): Promise<MyProductReview> {
  const res = await sendMutation(`/products/${encodeURIComponent(productId)}/reviews`, 'POST', data, signal)
  return parseMyReview(res)
}

export async function updateReview(
  reviewId: string,
  data: { rating: number; content: string },
  signal?: AbortSignal
): Promise<MyProductReview> {
  const res = await sendMutation(`/reviews/${encodeURIComponent(reviewId)}`, 'PATCH', data, signal)
  return parseMyReview(res)
}

export async function deleteReview(reviewId: string, signal?: AbortSignal): Promise<void> {
  await sendMutation(`/reviews/${encodeURIComponent(reviewId)}`, 'DELETE', undefined, signal)
}

export async function getMyReviews(signal?: AbortSignal): Promise<MyProductReview[]> {
  const data = await apiRequest('/reviews/mine', { signal, cache: 'no-store' })
  return list(data, parseMyReview)
}

export async function getModerationReviews(
  query?: { status?: 'ALL' | 'PUBLISHED' | 'HIDDEN'; search?: string; page?: number; pageSize?: number },
  signal?: AbortSignal
): Promise<ModerationProductReviewPage> {
  const params = new URLSearchParams()
  if (query?.status && query.status !== 'ALL') params.set('status', query.status)
  if (query?.search?.trim()) params.set('search', query.search.trim())
  if (query?.page) params.set('page', String(query.page))
  if (query?.pageSize) params.set('pageSize', String(query.pageSize))
  const qs = params.toString()
  const data = await apiRequest(`/moderation/reviews${qs ? `?${qs}` : ''}`, { signal, cache: 'no-store' })
  return parseModerationPage(data)
}

export async function moderateReview(
  reviewId: string,
  data: { action: 'HIDE' | 'RESTORE'; reason?: string },
  signal?: AbortSignal
): Promise<ModerationProductReview> {
  const res = await sendMutation(`/moderation/reviews/${encodeURIComponent(reviewId)}`, 'PATCH', data, signal)
  return parseModerationReview(res)
}

