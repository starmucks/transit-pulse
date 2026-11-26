import { describe, expect, it } from 'vitest'
import type { MapVehicle, MapVehicleDelta } from '../map/types'
import { noteLatencyDelta } from './noteLatencyDelta'
import { SourceToMapLatencyBuffer } from './sourceToMapLatency'

const t0 = '2026-11-26T18:00:00.000Z'
const t1 = '2026-11-26T18:00:10.000Z'

function vehicle(id: string, observedAt: string): MapVehicle {
  return {
    vehicleId: id,
    lon: 24.94,
    lat: 60.17,
    bearing: null,
    routeId: '1009',
    tripId: null,
    delaySec: null,
    observedAt,
  }
}

function delta(partial: Partial<MapVehicleDelta>): MapVehicleDelta {
  return {
    added: [],
    updated: [],
    removed: [],
    generation: 1,
    ...partial,
  }
}

describe('noteLatencyDelta', () => {
  it('does not sample Subscribe snapshot added vehicles', () => {
    const buffer = new SourceToMapLatencyBuffer()
    const last = new Map<string, string>()
    const receivedAt = Date.parse(t0) + 500

    noteLatencyDelta(buffer, last, delta({ added: [vehicle('v1', t0)] }), receivedAt)

    expect(buffer.size).toBe(0)
    expect(last.get('v1')).toBe(t0)
  })

  it('samples an updated vehicle with a new observedAt once', () => {
    const buffer = new SourceToMapLatencyBuffer()
    const last = new Map<string, string>([['v1', t0]])
    const receivedAt = Date.parse(t1) + 400

    noteLatencyDelta(buffer, last, delta({ updated: [vehicle('v1', t1)] }), receivedAt)
    noteLatencyDelta(buffer, last, delta({ updated: [vehicle('v1', t1)] }), receivedAt + 1_000)

    expect(buffer.size).toBe(1)
    expect(buffer.stats().p50Ms).toBe(400)
  })
})
