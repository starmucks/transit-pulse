import { fetchJson } from './fetchJson'
import type { RouteDto, RouteShapeDto, StopDto } from './types'

const DEFAULT_FEED = 'hsl'

/** Lists GTFS stops for the active snapshot. */
export function fetchStops(feedId = DEFAULT_FEED, signal?: AbortSignal): Promise<StopDto[]> {
  return fetchJson(`/api/v1/feeds/${encodeURIComponent(feedId)}/stops`, signal)
}

/** Lists GTFS routes for the active snapshot. */
export function fetchRoutes(feedId = DEFAULT_FEED, signal?: AbortSignal): Promise<RouteDto[]> {
  return fetchJson(`/api/v1/feeds/${encodeURIComponent(feedId)}/routes`, signal)
}

/** Lists shape polylines for one route. */
export function fetchRouteShapes(
  feedId: string,
  routeId: string,
  signal?: AbortSignal,
): Promise<RouteShapeDto[]> {
  return fetchJson(
    `/api/v1/feeds/${encodeURIComponent(feedId)}/routes/${encodeURIComponent(routeId)}/shapes`,
    signal,
  )
}
