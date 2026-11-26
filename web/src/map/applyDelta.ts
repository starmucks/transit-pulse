import type { MapVehicle, MapVehicleDelta } from './types'

/** Applies a viewport delta to an immutable Map keyed by vehicle id. */
export function applyDelta(
  previous: ReadonlyMap<string, MapVehicle>,
  delta: MapVehicleDelta,
): Map<string, MapVehicle> {
  const next = new Map(previous)

  for (const id of delta.removed) {
    next.delete(id)
  }

  for (const vehicle of delta.added) {
    next.set(vehicle.vehicleId, vehicle)
  }

  for (const vehicle of delta.updated) {
    next.set(vehicle.vehicleId, vehicle)
  }

  return next
}
