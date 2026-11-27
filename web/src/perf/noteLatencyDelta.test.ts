import { describe, expect, it } from 'vitest'
import type { MapVehicle, MapVehicleDelta } from '../map/types'
import { noteLatencyDelta, type SegmentLatencyBuffers } from './noteLatencyDelta'
import { SourceToMapLatencyBuffer } from './sourceToMapLatency'

const t0 = '2026-11-26T18:00:00.000Z'
const t1 = '2026-11-26T18:00:10.000Z'
const ingestedAt = '2026-11-26T18:00:11.000Z'
const redisAt = '2026-11-26T18:00:11.500Z'
const sentAt = '2026-11-26T18:00:11.700Z'

function vehicle(
  id: string,
  observedAt: string,
  redisWrittenAt: string | null = null,
  ingested: string | null = null,
): MapVehicle {
  return {
    vehicleId: id,
    lon: 24.94,
    lat: 60.17,
    bearing: null,
    routeId: '1009',
    tripId: null,
    delaySec: null,
    observedAt,
    redisWrittenAt,
    ingestedAt: ingested,
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

function buffers(): SegmentLatencyBuffers {
  return {
    total: new SourceToMapLatencyBuffer(),
    toRedis: new SourceToMapLatencyBuffer(),
    sourceAge: new SourceToMapLatencyBuffer(),
    ingestToRedis: new SourceToMapLatencyBuffer(),
    redisToSend: new SourceToMapLatencyBuffer(),
    sendToBrowser: new SourceToMapLatencyBuffer(),
  }
}

describe('noteLatencyDelta', () => {
  it('does not sample Subscribe snapshot added vehicles', () => {
    const b = buffers()
    const last = new Map<string, string>()
    const receivedAt = Date.parse(t0) + 500

    noteLatencyDelta(b, last, delta({ added: [vehicle('v1', t0)] }), receivedAt)

    expect(b.total.size).toBe(0)
    expect(last.get('v1')).toBe(t0)
  })

  it('samples an updated vehicle with a new observedAt once', () => {
    const b = buffers()
    const last = new Map<string, string>([['v1', t0]])
    const receivedAt = Date.parse(t1) + 400

    noteLatencyDelta(b, last, delta({ updated: [vehicle('v1', t1)] }), receivedAt)
    noteLatencyDelta(b, last, delta({ updated: [vehicle('v1', t1)] }), receivedAt + 1_000)

    expect(b.total.size).toBe(1)
    expect(b.total.stats().p50Ms).toBe(400)
    expect(b.toRedis.size).toBe(0)
  })

  it('records segment ages when redisWrittenAt, ingestedAt and sentAt are present', () => {
    const b = buffers()
    const last = new Map<string, string>([['v1', t0]])
    const receivedAt = Date.parse(sentAt) + 50

    noteLatencyDelta(
      b,
      last,
      delta({
        updated: [vehicle('v1', t1, redisAt, ingestedAt)],
        sentAt,
      }),
      receivedAt,
    )

    expect(b.total.stats().p50Ms).toBe(receivedAt - Date.parse(t1))
    expect(b.toRedis.stats().p50Ms).toBe(Date.parse(redisAt) - Date.parse(t1))
    expect(b.sourceAge.stats().p50Ms).toBe(Date.parse(ingestedAt) - Date.parse(t1))
    expect(b.ingestToRedis.stats().p50Ms).toBe(Date.parse(redisAt) - Date.parse(ingestedAt))
    expect(b.redisToSend.stats().p50Ms).toBe(Date.parse(sentAt) - Date.parse(redisAt))
    expect(b.sendToBrowser.stats().p50Ms).toBe(50)
  })
})
