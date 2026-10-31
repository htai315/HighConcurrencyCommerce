const normalizeBaseUrl = (value: string) => value.replace(/\/$/, '')

export const env = {
  apiBaseUrl: normalizeBaseUrl(import.meta.env.VITE_API_BASE_URL ?? '/api/v1'),
} as const
