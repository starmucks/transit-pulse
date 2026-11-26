import { describe, expect, it } from 'vitest'
import { shapesToGeoJson } from './shapesToGeoJson'

describe('shapesToGeoJson', () => {
  it('drops shapes with fewer than two points', () => {
    const geojson = shapesToGeoJson([
      { shapeId: 'short', points: [{ lat: 60.1, lon: 24.9 }] },
      {
        shapeId: 'ok',
        points: [
          { lat: 60.1, lon: 24.9 },
          { lat: 60.2, lon: 25.0 },
        ],
      },
    ])

    expect(geojson.features).toHaveLength(1)
    expect(geojson.features[0]?.properties.shapeId).toBe('ok')
    expect(geojson.features[0]?.geometry.coordinates).toEqual([
      [24.9, 60.1],
      [25.0, 60.2],
    ])
  })
})
