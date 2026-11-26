import type { ReactNode } from 'react'

type QueryStateProps = {
  isPending: boolean
  isError: boolean
  errorMessage: string | null
  isEmpty: boolean
  emptyMessage: string
  children: ReactNode
}

/**
 * Shared loading / empty / error chrome so panels never fail silently.
 */
export function QueryState({
  isPending,
  isError,
  errorMessage,
  isEmpty,
  emptyMessage,
  children,
}: QueryStateProps) {
  if (isPending) {
    return <p className="panel-state">Loading…</p>
  }
  if (isError) {
    return <p className="panel-state panel-state-error">{errorMessage ?? 'Request failed'}</p>
  }
  if (isEmpty) {
    return <p className="panel-state">{emptyMessage}</p>
  }
  return <>{children}</>
}
