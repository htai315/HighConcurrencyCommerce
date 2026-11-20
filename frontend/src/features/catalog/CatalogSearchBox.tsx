import { useEffect, useId, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { getSearchSuggestions } from './catalog.api'
import type { SearchSuggestion } from './catalog.api'
import {
  buildCatalogSearchUrl,
  clearCatalogSearchHistory,
  readCatalogSearchHistory,
  rememberCatalogSearch,
  removeCatalogSearchHistory,
} from './catalog-search'
import './catalog-search-box.css'

type CatalogSearchBoxProps = {
  ariaLabel: string
  className?: string
  fieldMode?: boolean
  placeholder?: string
  preserveFilters?: boolean
}

type RequestState = 'idle' | 'loading' | 'ready' | 'empty' | 'error'

const KIND_LABEL: Record<SearchSuggestion['kind'], string> = {
  PRODUCT: 'Sản phẩm',
  SKU: 'Mã SKU',
  BRAND: 'Thương hiệu',
  CATEGORY: 'Danh mục',
}

function SearchIcon() {
  return <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></svg>
}

export function CatalogSearchBox({
  ariaLabel,
  className = '',
  fieldMode = false,
  placeholder = 'Tìm kiếm vợt, giày, trang phục, mã sản phẩm…',
  preserveFilters = true,
}: CatalogSearchBoxProps) {
  const location = useLocation()
  const navigate = useNavigate()
  const initialQuery = location.pathname === '/' ? new URLSearchParams(location.search).get('q') ?? '' : ''
  const [value, setValue] = useState(initialQuery)
  const [suggestions, setSuggestions] = useState<SearchSuggestion[]>([])
  const [requestState, setRequestState] = useState<RequestState>('idle')
  const [open, setOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(-1)
  const [history, setHistory] = useState<string[]>(() => readCatalogSearchHistory())
  const [retryRevision, setRetryRevision] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const requestRevision = useRef(0)
  const lastLocation = useRef(`${location.pathname}${location.search}`)
  const instanceId = useId().replace(/:/g, '')
  const listboxId = `catalog-search-list-${instanceId}`
  const normalizedValue = value.trim()
  const showingHistory = open && normalizedValue.length === 0 && history.length > 0
  const showingSuggestions = open && normalizedValue.length >= 2
  const hasPopup = showingHistory || showingSuggestions

  useEffect(() => {
    const locationKey = `${location.pathname}${location.search}`
    if (lastLocation.current === locationKey) return
    lastLocation.current = locationKey
    if (location.pathname !== '/') return
    const timer = window.setTimeout(() => setValue(new URLSearchParams(location.search).get('q') ?? ''), 0)
    return () => window.clearTimeout(timer)
  }, [location.pathname, location.search])

  useEffect(() => {
    const currentRevision = ++requestRevision.current
    if (normalizedValue.length < 2) return
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      setRequestState('loading')
      void getSearchSuggestions(normalizedValue, controller.signal)
        .then(items => {
          if (currentRevision !== requestRevision.current) return
          setSuggestions(items)
          setRequestState(items.length ? 'ready' : 'empty')
          setActiveIndex(-1)
        })
        .catch(error => {
          if (error instanceof DOMException && error.name === 'AbortError') return
          if (currentRevision !== requestRevision.current) return
          setSuggestions([])
          setRequestState('error')
          setActiveIndex(-1)
        })
    }, 250)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [normalizedValue, retryRevision])

  useEffect(() => {
    if (!open) return
    function handlePointerDown(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', handlePointerDown)
    return () => document.removeEventListener('pointerdown', handlePointerDown)
  }, [open])

  function submitSearch(query: string) {
    const trimmed = query.trim().replace(/\s+/g, ' ')
    if (trimmed) setHistory(rememberCatalogSearch(trimmed))
    const current = preserveFilters && location.pathname === '/' ? new URLSearchParams(location.search) : new URLSearchParams()
    setOpen(false)
    setActiveIndex(-1)
    void navigate(buildCatalogSearchUrl(current, trimmed))
  }

  function chooseSuggestion(item: SearchSuggestion) {
    setHistory(rememberCatalogSearch(item.queryValue))
    setOpen(false)
    setActiveIndex(-1)
    if (item.productId && (item.kind === 'PRODUCT' || item.kind === 'SKU')) {
      void navigate(`/products/${encodeURIComponent(item.productId)}`)
      return
    }
    setValue(item.queryValue)
    submitSearch(item.queryValue)
  }

  function selectableCount() {
    if (normalizedValue.length === 0) return history.length
    if (normalizedValue.length >= 2 && requestState === 'ready') return suggestions.length
    return 0
  }

  function handleKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    const count = selectableCount()
    if (event.key === 'ArrowDown' && count > 0) {
      event.preventDefault()
      setOpen(true)
      setActiveIndex(index => index >= count - 1 ? 0 : index + 1)
    } else if (event.key === 'ArrowUp' && count > 0) {
      event.preventDefault()
      setOpen(true)
      setActiveIndex(index => index <= 0 ? count - 1 : index - 1)
    } else if (event.key === 'Escape') {
      event.preventDefault()
      setOpen(false)
      setActiveIndex(-1)
    } else if (event.key === 'Enter') {
      event.preventDefault()
      if (activeIndex >= 0) {
        if (showingHistory) submitSearch(history[activeIndex] ?? value)
        else if (suggestions[activeIndex]) chooseSuggestion(suggestions[activeIndex])
      } else submitSearch(value)
    }
  }

  const activeDescendant = activeIndex >= 0 && hasPopup ? `${listboxId}-option-${activeIndex}` : undefined
  const field = <div className={`catalog-search-box ${className}`.trim()} ref={rootRef}>
    <div className="catalog-search-control">
      <input
        ref={inputRef}
        aria-autocomplete="list"
        aria-controls={hasPopup ? listboxId : undefined}
        aria-expanded={hasPopup}
        aria-activedescendant={activeDescendant}
        aria-label={ariaLabel}
        autoComplete="off"
        maxLength={100}
        name="q"
        placeholder={placeholder}
        role="combobox"
        type="search"
        value={value}
        onChange={event => {
          const nextValue = event.target.value
          setValue(nextValue)
          if (nextValue.trim().length < 2) {
            setSuggestions([])
            setRequestState('idle')
          }
          setOpen(true)
          setActiveIndex(-1)
        }}
        onFocus={() => {
          setHistory(readCatalogSearchHistory())
          setOpen(true)
        }}
        onKeyDown={handleKeyDown}
      />
      {!fieldMode && <button type="button" aria-label="Tìm kiếm" onClick={() => submitSearch(value)}><SearchIcon /></button>}
    </div>
    {showingHistory && <div className="catalog-search-popover" id={listboxId} role="listbox" aria-label="Tìm kiếm gần đây">
      <div className="catalog-search-popover-heading"><span>Tìm kiếm gần đây</span><button type="button" onClick={() => { clearCatalogSearchHistory(); setHistory([]); setOpen(false) }}>Xóa tất cả</button></div>
      {history.map((item, index) => <div className={`catalog-search-history-row${activeIndex === index ? ' active' : ''}`} role="presentation" key={item}>
        <button type="button" className="catalog-search-history-value" id={`${listboxId}-option-${index}`} role="option" aria-selected={activeIndex === index} onMouseDown={event => event.preventDefault()} onClick={() => submitSearch(item)}><span aria-hidden="true">↺</span>{item}</button>
        <button type="button" className="catalog-search-history-remove" aria-label={`Xóa ${item} khỏi lịch sử`} onClick={() => setHistory(removeCatalogSearchHistory(item))}>×</button>
      </div>)}
      <p className="catalog-search-device-note">Lịch sử chỉ lưu trên trình duyệt này.</p>
    </div>}
    {showingSuggestions && <div className="catalog-search-popover" id={listboxId} role="listbox" aria-label="Gợi ý tìm kiếm">
      {requestState === 'loading' && <div className="catalog-search-loading" role="status"><span /><span /><span /><em>Đang tìm gợi ý…</em></div>}
      {requestState === 'empty' && <div className="catalog-search-message">Không có gợi ý phù hợp. Nhấn Enter để tìm “{normalizedValue}”.</div>}
      {requestState === 'error' && <div className="catalog-search-message" role="alert"><span>Chưa tải được gợi ý.</span><button type="button" onClick={() => setRetryRevision(value => value + 1)}>Thử lại</button></div>}
      {requestState === 'ready' && suggestions.map((item, index) => <button
        type="button"
        className={`catalog-search-option${activeIndex === index ? ' active' : ''}`}
        id={`${listboxId}-option-${index}`}
        role="option"
        aria-selected={activeIndex === index}
        key={`${item.kind}-${item.label}-${item.productId ?? ''}`}
        onMouseDown={event => event.preventDefault()}
        onMouseEnter={() => setActiveIndex(index)}
        onClick={() => chooseSuggestion(item)}
      >
        <span className="catalog-search-kind">{KIND_LABEL[item.kind]}</span>
        <span className="catalog-search-option-copy"><strong>{item.label}</strong>{item.secondaryText && <small>{item.secondaryText}</small>}</span>
        <span className="catalog-search-option-arrow" aria-hidden="true">→</span>
      </button>)}
    </div>}
  </div>

  if (fieldMode) return field
  return <form role="search" aria-label={ariaLabel} onSubmit={event => { event.preventDefault(); submitSearch(value) }}>{field}</form>
}
