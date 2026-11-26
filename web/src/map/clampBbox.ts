import type { GeoBoundingBox } from './types'

/** Matches MapViewportLimits.MaxSpanDegrees on the API (ADR-0017). */
export const MAX_SPAN_DEGREES = 0.5

/**
 * Shrinks a viewport box so longitude and latitude spans stay within the
 * API limit, keeping the geographic centre of the visible map.
 */
export function clampBbox(box: GeoBoundingBox, maxSpan = MAX_SPAN_DEGREES): GeoBoundingBox {
  const lonSpan = box.east - box.west
  const latSpan = box.north - box.south
  const lonMid = (box.west + box.east) / 2
  const latMid = (box.south + box.north) / 2

  const halfLon = Math.min(lonSpan, maxSpan) / 2
  const halfLat = Math.min(latSpan, maxSpan) / 2

  return {
    west: lonMid - halfLon,
    east: lonMid + halfLon,
    south: latMid - halfLat,
    north: latMid + halfLat,
  }
}
