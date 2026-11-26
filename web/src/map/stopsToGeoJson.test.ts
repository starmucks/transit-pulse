import { describe, expect, it } from 'vitest'
import { stopsToGeoJson } from './stopsToGeoJson'

describe('stopsToGeoJson', () => {
  it('keeps boarding stops and drops stations', () => {
    const geojson = stopsToGeoJson([
      {
        stopId: 'A',
        name: 'Platform',
        lat: 60.1,
        lon: 24.9,
        locationType: 0,
        parentStation: 'S',
      },
      {
        stopId: 'S',
        name: 'Station',
        lat: 60.1,
        lon: 24.9,
        locationType: 1,
        parentStation: null,
      },
    ])

    expect(geojson.features).toHaveLength(1)
    expect(geojson.features[0]?.properties.stopId).toBe('A')
  })
})
