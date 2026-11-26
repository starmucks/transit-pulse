import type { MapVehicle, MapVehicleDelta } from '../map/types'
import type { SourceToMapLatencyBuffer } from './sourceToMapLatency'

/**
 * Feeds one accepted SignalR delta into the latency buffer.
 * Snapshots (`added`) only seed last-seen times; samples come from `updated`
 * with a new `observedAt` so 1 Hz repeats and Redis-stale first paint are ignored.
 */
export function noteLatencyDelta(
  buffer: SourceToMapLatencyBuffer,
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
    recordIfNew(buffer, lastObserved, vehicle, receivedAtMs)
  }
}

function recordIfNew(
  buffer: SourceToMapLatencyBuffer,
  lastObserved: Map<string, string>,
  vehicle: MapVehicle,
  receivedAt: number,
): void {
  if (lastObserved.get(vehicle.vehicleId) === vehicle.observedAt) {
    return
  }
  lastObserved.set(vehicle.vehicleId, vehicle.observedAt)
  buffer.record(vehicle.observedAt, receivedAt)
}
