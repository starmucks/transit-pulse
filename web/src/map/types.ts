/** One vehicle on the map — mirrors API MapVehicle (camelCase JSON). */
export type MapVehicle = {
  vehicleId: string
  lon: number
  lat: number
  bearing: number | null
  routeId: string | null
  tripId: string | null
  delaySec: number | null
  observedAt: string
  /** When Worker wrote the Redis hash; absent on stale hashes. */
  redisWrittenAt?: string | null
}

/** One SignalR frame — mirrors API MapVehicleDelta. */
export type MapVehicleDelta = {
  added: MapVehicle[]
  updated: MapVehicle[]
  removed: string[]
  /** Must match the generation returned by the last successful Subscribe. */
  generation: number
  /** UTC when the API stamped this frame before SendAsync (C3 segment ages). */
  sentAt?: string
}

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export type GeoBoundingBox = {
  west: number
  south: number
  east: number
  north: number
}
