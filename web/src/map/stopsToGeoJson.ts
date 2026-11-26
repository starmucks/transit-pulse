import type { StopDto } from '../api/types'

export type StopFeatureCollection = {
  type: 'FeatureCollection'
  features: Array<{
    type: 'Feature'
    id: string
    properties: {
      stopId: string
      name: string | null
    }
    geometry: { type: 'Point'; coordinates: [number, number] }
  }>
}

/**
 * Boarding stops only (`location_type` 0). Stations and entrances stay out of
 * the click layer so arrivals queries hit real stop ids.
 */
export function stopsToGeoJson(stops: readonly StopDto[]): StopFeatureCollection {
  const features: StopFeatureCollection['features'] = []
  for (const stop of stops) {
    if (stop.locationType !== 0) {
      continue
    }
    features.push({
      type: 'Feature',
      id: stop.stopId,
      properties: {
        stopId: stop.stopId,
        name: stop.name,
      },
      geometry: {
        type: 'Point',
        coordinates: [stop.lon, stop.lat],
      },
    })
  }
  return { type: 'FeatureCollection', features }
}
