import { useSearchParams } from 'react-router-dom'

/** Keep navigation shareable and preserve browser back/forward history. */
export function useWorkspaceSection<T extends string>(sections: readonly T[], fallback: T) {
  const [params, setParams] = useSearchParams()
  const requested = params.get('section')
  const active = sections.find(section => section === requested) ?? fallback
  function select(section: T) {
    setParams(previous => {
      const next = new URLSearchParams(previous)
      next.set('section', section)
      return next
    }, { preventScrollReset: true })
  }
  return [active, select] as const
}
