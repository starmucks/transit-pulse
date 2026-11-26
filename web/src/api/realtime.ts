import { fetchJson } from './fetchJson'
import type { RoutePunctualityDto, StopEventDto } from './types'

const DEFAULT_FEED = 'hsl'

/** Recent observed stop events (default lookback is enforced by the API). */
export function fetchStopEvents(
  stopId: string,
  feedId = DEFAULT_FEED,
  signal?: AbortSignal,
): Promise<StopEventDto[]> {
  return fetchJson(
    `/api/v1/feeds/${encodeURIComponent(feedId)}/stops/${encodeURIComponent(stopId)}/events`,
    signal,
  )
}

/** One UTC day of route punctuality (default: today UTC). */
export function fetchRoutePunctuality(
  routeId: string,
  feedId = DEFAULT_FEED,
  signal?: AbortSignal,
): Promise<RoutePunctualityDto> {
  return fetchJson(
    `/api/v1/feeds/${encodeURIComponent(feedId)}/routes/${encodeURIComponent(routeId)}/punctuality`,
    signal,
  )
}
