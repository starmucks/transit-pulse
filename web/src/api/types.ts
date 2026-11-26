/** GTFS stop from the catalogue API. */
export type StopDto = {
  stopId: string
  name: string | null
  lat: number
  lon: number
  locationType: number
  parentStation: string | null
}

/** GTFS route from the catalogue API. */
export type RouteDto = {
  routeId: string
  shortName: string | null
  longName: string | null
  routeType: number
}

/** One vertex of a route shape. */
export type ShapePointDto = {
  lat: number
  lon: number
}

/** One GTFS shape polyline. */
export type RouteShapeDto = {
  shapeId: string
  points: ShapePointDto[]
}

/** Observed stop event (not a schedule ETA). */
export type StopEventDto = {
  id: string
  vehicleId: string
  tripId: string
  routeId: string
  directionId: number
  serviceDate: string
  stopId: string
  stopSequence: number
  kind: string
  at: string
  delaySec: number | null
}

/** One UTC day of route punctuality from the CAGG. */
export type RoutePunctualityDto = {
  day: string
  arrivals: number
  onTimeArrivals: number
  avgArrivalDelaySec: number | null
  onTimeEarlySec: number
  onTimeLateSec: number
}
