import type { RouteShapeDto } from '../api/types'

export type ShapeFeatureCollection = {
  type: 'FeatureCollection'
  features: Array<{
    type: 'Feature'
    id: string
    properties: { shapeId: string }
    geometry: { type: 'LineString'; coordinates: [number, number][] }
  }>
}

/** Converts catalogue shape polylines into a MapLibre GeoJSON source. */
export function shapesToGeoJson(shapes: readonly RouteShapeDto[]): ShapeFeatureCollection {
  return {
    type: 'FeatureCollection',
    features: shapes
      .filter((shape) => shape.points.length >= 2)
      .map((shape) => ({
        type: 'Feature' as const,
        id: shape.shapeId,
        properties: { shapeId: shape.shapeId },
        geometry: {
          type: 'LineString' as const,
          coordinates: shape.points.map((point) => [point.lon, point.lat] as [number, number]),
        },
      })),
  }
}
