import { describe, expect, it } from 'vitest'
import { applyDelta } from './applyDelta'
import type { MapVehicle, MapVehicleDelta } from './types'

const at = '2026-11-26T12:00:00Z'

function vehicle(id: string, lon: number, lat: number): MapVehicle {
  return {
    vehicleId: id,
    lon,
    lat,
    bearing: null,
    routeId: '1009',
    tripId: null,
    delaySec: null,
    observedAt: at,
  }
}

describe('applyDelta', () => {
  it('adds, updates and removes by vehicle id', () => {
    const previous = new Map<string, MapVehicle>([
      ['a', vehicle('a', 24.9, 60.1)],
      ['b', vehicle('b', 24.91, 60.11)],
    ])
    const delta: MapVehicleDelta = {
      added: [vehicle('c', 24.93, 60.12)],
      updated: [vehicle('b', 24.92, 60.11)],
      removed: ['a'],
      generation: 1,
    }

    const next = applyDelta(previous, delta)

    expect([...next.keys()].sort()).toEqual(['b', 'c'])
    expect(next.get('b')?.lon).toBe(24.92)
    expect(previous.has('a')).toBe(true)
  })

  it('returns a new map when the delta is empty', () => {
    const previous = new Map([['a', vehicle('a', 24.9, 60.1)]])
    const next = applyDelta(previous, {
      added: [],
      updated: [],
      removed: [],
      generation: 2,
    })

    expect(next).not.toBe(previous)
    expect(next.get('a')).toEqual(previous.get('a'))
  })
})
