import { useQuery } from '@tanstack/react-query'
import { ApiNotFoundError } from '../api/fetchJson'
import { formatClock, formatDelay } from '../api/format'
import { fetchStopEvents } from '../api/realtime'
import type { StopDto } from '../api/types'
import { QueryState } from './QueryState'

type StopPanelProps = {
  feedId: string
  stop: StopDto
  onClose: () => void
  onOpenRoute: (routeId: string) => void
}

/**
 * Side panel for one stop: recent observed arrivals/departures from REST 3.4.
 */
export function StopPanel({ feedId, stop, onClose, onOpenRoute }: StopPanelProps) {
  const query = useQuery({
    queryKey: ['stop-events', feedId, stop.stopId],
    queryFn: ({ signal }) => fetchStopEvents(stop.stopId, feedId, signal),
  })

  const errorMessage =
    query.error instanceof ApiNotFoundError
      ? 'Stop not found in the active GTFS snapshot.'
      : query.error instanceof Error
        ? query.error.message
        : null

  return (
    <aside className="side-panel" aria-label={`Stop ${stop.name ?? stop.stopId}`}>
      <header className="side-panel-header">
        <div>
          <p className="side-panel-kicker">Stop</p>
          <h2 className="side-panel-title">{stop.name ?? stop.stopId}</h2>
          <p className="side-panel-meta">id {stop.stopId}</p>
        </div>
        <button type="button" className="side-panel-close" onClick={onClose} aria-label="Close stop panel">
          ×
        </button>
      </header>

      <QueryState
        isPending={query.isPending}
        isError={query.isError}
        errorMessage={errorMessage}
        isEmpty={!query.isPending && !query.isError && (query.data?.length ?? 0) === 0}
        emptyMessage="No observed arrivals in the last hour."
      >
        <ul className="event-list">
          {(query.data ?? []).map((event) => (
            <li key={event.id} className="event-row">
              <div className="event-main">
                <button
                  type="button"
                  className="event-route"
                  onClick={() => onOpenRoute(event.routeId)}
                >
                  {event.routeId}
                </button>
                <span className="event-kind">{event.kind}</span>
              </div>
              <div className="event-meta">
                <span>{formatClock(event.at)}</span>
                <span>{formatDelay(event.delaySec)}</span>
              </div>
            </li>
          ))}
        </ul>
      </QueryState>
    </aside>
  )
}
