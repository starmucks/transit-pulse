import type { MapVehicle, MapVehicleDelta } from '../map/types'
import type { SourceToMapLatencyBuffer } from './sourceToMapLatency'

/** Per-segment buffers for C3: total and three pipeline cuts. */
export type SegmentLatencyBuffers = {
  total: SourceToMapLatencyBuffer
  toRedis: SourceToMapLatencyBuffer
  redisToSend: SourceToMapLatencyBuffer
  sendToBrowser: SourceToMapLatencyBuffer
}

/**
 * Feeds one accepted SignalR delta into the latency buffers.
 * Snapshots (`added`) only seed last-seen times; samples come from `updated`
 * with a new `observedAt` so ticker repeats of the same observation are ignored.
 *
 * Segments (when stamps are present):
 * - total: browser − observedAt
 * - toRedis: redisWrittenAt − observedAt
 * - redisToSend: sentAt − redisWrittenAt
 * - sendToBrowser: browser − sentAt
 */
export function noteLatencyDelta(
  buffers: SegmentLatencyBuffers,
  lastObserved: Map<string, string>,
  delta: MapVehicleDelta,
  receivedAtMs: number,
): void {
  for (const id of delta.removed) {
    lastObserved.delete(id)
  }
  for (const vehicle of delta.added) {
    lastObserved.set(vehicle.vehicleId, vehicle.observedAt)
  }
  for (const vehicle of delta.updated) {
    recordIfNew(buffers, lastObserved, vehicle, delta.sentAt, receivedAtMs)
  }
}

function recordIfNew(
  buffers: SegmentLatencyBuffers,
  lastObserved: Map<string, string>,
  vehicle: MapVehicle,
  sentAtIso: string | undefined,
  receivedAt: number,
): void {
  if (lastObserved.get(vehicle.vehicleId) === vehicle.observedAt) {
    return
  }
  lastObserved.set(vehicle.vehicleId, vehicle.observedAt)
  buffers.total.record(vehicle.observedAt, receivedAt)

  const observedMs = Date.parse(vehicle.observedAt)
  if (Number.isNaN(observedMs)) {
    return
  }

  const redisMs = vehicle.redisWrittenAt ? Date.parse(vehicle.redisWrittenAt) : Number.NaN
  if (!Number.isNaN(redisMs)) {
    buffers.toRedis.recordDelayMs(redisMs - observedMs)
  }

  const sentMs = sentAtIso ? Date.parse(sentAtIso) : Number.NaN
  if (!Number.isNaN(redisMs) && !Number.isNaN(sentMs)) {
    buffers.redisToSend.recordDelayMs(sentMs - redisMs)
  }
  if (!Number.isNaN(sentMs)) {
    buffers.sendToBrowser.recordDelayMs(receivedAt - sentMs)
  }
}
