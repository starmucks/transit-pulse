import { useQuery } from '@tanstack/react-query'
import { fetchRoutes } from '../api/catalogue'
import { ApiNotFoundError } from '../api/fetchJson'
import { formatAvgDelay, formatOnTimeShare } from '../api/format'
import { fetchRoutePunctuality } from '../api/realtime'
import { QueryState } from './QueryState'

type RoutePanelProps = {
  feedId: string
  routeId: string
  vehicleCount: number
  onClose: () => void
}

/**
 * Side panel for route mode: punctuality for the UTC day + live vehicle count.
 */
export function RoutePanel({ feedId, routeId, vehicleCount, onClose }: RoutePanelProps) {
  const routesQuery = useQuery({
    queryKey: ['routes', feedId],
    queryFn: ({ signal }) => fetchRoutes(feedId, signal),
    staleTime: 10 * 60_000,
  })

  const punctualityQuery = useQuery({
    queryKey: ['route-punctuality', feedId, routeId],
    queryFn: ({ signal }) => fetchRoutePunctuality(routeId, feedId, signal),
  })

  const route = routesQuery.data?.find((item) => item.routeId === routeId)
  const title = route?.shortName ?? route?.longName ?? routeId
  const subtitle = route?.longName && route.shortName ? route.longName : `id ${routeId}`

  const errorMessage =
    punctualityQuery.error instanceof ApiNotFoundError
      ? 'Route not found in the active GTFS snapshot.'
      : punctualityQuery.error instanceof Error
        ? punctualityQuery.error.message
        : null

  const summary = punctualityQuery.data

  return (
    <aside className="side-panel" aria-label={`Route ${title}`}>
      <header className="side-panel-header">
        <div>
          <p className="side-panel-kicker">Route</p>
          <h2 className="side-panel-title">{title}</h2>
          <p className="side-panel-meta">{subtitle}</p>
        </div>
        <button type="button" className="side-panel-close" onClick={onClose} aria-label="Close route panel">
          ×
        </button>
      </header>

      <p className="route-live">{vehicleCount} vehicles on this route</p>

      <QueryState
        isPending={punctualityQuery.isPending}
        isError={punctualityQuery.isError}
        errorMessage={errorMessage}
        isEmpty={false}
        emptyMessage=""
      >
        {summary ? (
          <dl className="punctuality-grid">
            <div>
              <dt>UTC day</dt>
              <dd>{summary.day}</dd>
            </div>
            <div>
              <dt>Arrivals</dt>
              <dd>{summary.arrivals}</dd>
            </div>
            <div>
              <dt>On time</dt>
              <dd>{formatOnTimeShare(summary.onTimeArrivals, summary.arrivals)}</dd>
            </div>
            <div>
              <dt>Avg delay</dt>
              <dd>{formatAvgDelay(summary.avgArrivalDelaySec)}</dd>
            </div>
          </dl>
        ) : null}
      </QueryState>
    </aside>
  )
}
