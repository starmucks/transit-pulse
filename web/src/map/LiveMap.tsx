import { useQuery } from '@tanstack/react-query'
import { useCallback, useMemo, useRef, useState } from 'react'
import Map, {
  Layer,
  NavigationControl,
  Source,
  type MapLayerMouseEvent,
  type MapRef,
  type ViewStateChangeEvent,
} from 'react-map-gl/maplibre'
import 'maplibre-gl/dist/maplibre-gl.css'
import { fetchRouteShapes, fetchStops } from '../api/catalogue'
import type { StopDto } from '../api/types'
import { isLatencyBenchEnabled, useLatencyBench } from '../perf/useLatencyBench'
import { RoutePanel } from '../panels/RoutePanel'
import { StopPanel } from '../panels/StopPanel'
import { shapesToGeoJson } from './shapesToGeoJson'
import { stopsToGeoJson } from './stopsToGeoJson'
import type { GeoBoundingBox, MapVehicle } from './types'
import { useVehicleHub } from './useVehicleHub'

const HELSINKI = { longitude: 24.941, latitude: 60.171, zoom: 13.2 }
/** Fallback bbox around the initial Helsinki view so Subscribe works even before onLoad. */
const HELSINKI_VIEWPORT: GeoBoundingBox = {
  west: 24.88,
  south: 60.14,
  east: 25.0,
  north: 60.2,
}
const STYLE_URL = 'https://tiles.openfreemap.org/styles/liberty'
const VEHICLE_SOURCE_ID = 'vehicles'
const CIRCLE_LAYER_ID = 'vehicles-circle'
const LABEL_LAYER_ID = 'vehicles-label'
const STOP_SOURCE_ID = 'stops'
const STOP_LAYER_ID = 'stops-circle'
const SHAPE_SOURCE_ID = 'route-shapes'
const SHAPE_LAYER_ID = 'route-shapes-line'

/** Matches circle-color thresholds in the vehicles layer. */
const DELAY_LEGEND = [
  { color: '#0f7a6e', label: 'On time (≤1 min)' },
  { color: '#c98a1a', label: 'Delayed (1–3 min)' },
  { color: '#b33b5a', label: 'Late (>3 min)' },
] as const

type LiveMapProps = {
  feedId?: string
}

type Selection =
  | { kind: 'stop'; stop: StopDto }
  | { kind: 'route'; routeId: string }

type VehicleFeatureCollection = {
  type: 'FeatureCollection'
  features: Array<{
    type: 'Feature'
    id: string
    properties: {
      vehicleId: string
      routeId: string | null
      delaySec: number | null
      bearing: number | null
    }
    geometry: { type: 'Point'; coordinates: [number, number] }
  }>
}

/**
 * Full-bleed Helsinki map: viewport-following SignalR subscription, stop click
 * → arrivals panel, vehicle/route click → route mode with punctuality.
 */
export function LiveMap({ feedId = 'hsl' }: LiveMapProps) {
  const mapRef = useRef<MapRef>(null)
  const [viewport, setViewport] = useState<GeoBoundingBox | null>(HELSINKI_VIEWPORT)
  const [selection, setSelection] = useState<Selection | null>(null)
  const routeId = selection?.kind === 'route' ? selection.routeId : null
  const benchEnabled = isLatencyBenchEnabled()
  const { stats: latencyStats, noteSample } = useLatencyBench(benchEnabled)

  const { vehicles, status, error, vehicleCount } = useVehicleHub({
    feedId,
    viewport,
    routeId,
    onAcceptedDelta: noteSample,
  })

  const stopsQuery = useQuery({
    queryKey: ['stops', feedId],
    queryFn: ({ signal }) => fetchStops(feedId, signal),
    staleTime: 10 * 60_000,
  })

  const shapesQuery = useQuery({
    queryKey: ['route-shapes', feedId, routeId],
    queryFn: ({ signal }) => fetchRouteShapes(feedId, routeId!, signal),
    enabled: routeId !== null,
    staleTime: 10 * 60_000,
  })

  const stopById = useMemo(() => {
    const byId = new globalThis.Map<string, StopDto>()
    for (const stop of stopsQuery.data ?? []) {
      byId.set(stop.stopId, stop)
    }
    return byId
  }, [stopsQuery.data])

  const vehicleGeoJson = useMemo(() => vehiclesToGeoJson(vehicles), [vehicles])
  const stopGeoJson = useMemo(() => stopsToGeoJson(stopsQuery.data ?? []), [stopsQuery.data])
  const shapeGeoJson = useMemo(
    () => shapesToGeoJson(shapesQuery.data ?? []),
    [shapesQuery.data],
  )

  const onLoad = useCallback(() => {
    const map = mapRef.current
    if (!map) {
      return
    }
    setViewport(boundsFromMap(map))
  }, [])

  const onMoveEnd = useCallback((event: ViewStateChangeEvent) => {
    setViewport(boundsFromMap(event.target))
  }, [])

  const openRoute = useCallback((nextRouteId: string) => {
    setSelection({ kind: 'route', routeId: nextRouteId })
  }, [])

  const onClick = useCallback(
    (event: MapLayerMouseEvent) => {
      const vehicleHit = event.features?.find((feature) => feature.layer?.id === CIRCLE_LAYER_ID)
      if (vehicleHit) {
        const nextRouteId = vehicleHit.properties?.routeId
        if (typeof nextRouteId === 'string' && nextRouteId.length > 0) {
          openRoute(nextRouteId)
          return
        }
      }

      const stopHit = event.features?.find((feature) => feature.layer?.id === STOP_LAYER_ID)
      if (stopHit) {
        const stopId = stopHit.properties?.stopId
        if (typeof stopId === 'string') {
          const stop = stopById.get(stopId)
          if (stop) {
            setSelection({ kind: 'stop', stop })
          }
        }
        return
      }

      setSelection(null)
    },
    [openRoute, stopById],
  )

  const tagline =
    selection?.kind === 'route'
      ? `Route ${selection.routeId} · live vehicles + punctuality`
      : selection?.kind === 'stop'
        ? `Stop ${selection.stop.name ?? selection.stop.stopId}`
        : 'Live Helsinki · click a stop or a vehicle'

  return (
    <div className="live-map">
      <Map
        ref={mapRef}
        initialViewState={HELSINKI}
        mapStyle={STYLE_URL}
        attributionControl={false}
        interactiveLayerIds={[CIRCLE_LAYER_ID, STOP_LAYER_ID]}
        onLoad={onLoad}
        onMoveEnd={onMoveEnd}
        onClick={onClick}
        cursor="pointer"
        style={{ width: '100%', height: '100%' }}
      >
        <NavigationControl position="bottom-right" showCompass={false} />

        {routeId && shapeGeoJson.features.length > 0 ? (
          <Source id={SHAPE_SOURCE_ID} type="geojson" data={shapeGeoJson}>
            <Layer
              id={SHAPE_LAYER_ID}
              type="line"
              paint={{
                'line-color': '#0f7a6e',
                'line-width': 3.5,
                'line-opacity': 0.55,
              }}
            />
          </Source>
        ) : null}

        <Source id={STOP_SOURCE_ID} type="geojson" data={stopGeoJson}>
          <Layer
            id={STOP_LAYER_ID}
            type="circle"
            minzoom={13}
            paint={{
              'circle-radius': ['interpolate', ['linear'], ['zoom'], 13, 2.2, 16, 4.5],
              'circle-color': '#f3efe6',
              'circle-stroke-width': 1,
              'circle-stroke-color': '#4a6570',
              'circle-opacity': 0.9,
            }}
          />
        </Source>

        <Source id={VEHICLE_SOURCE_ID} type="geojson" data={vehicleGeoJson}>
          <Layer
            id={CIRCLE_LAYER_ID}
            type="circle"
            paint={{
              'circle-radius': ['interpolate', ['linear'], ['zoom'], 11, 3.5, 14, 6, 16, 8],
              'circle-color': [
                'case',
                ['>', ['coalesce', ['get', 'delaySec'], 0], 180],
                '#b33b5a',
                ['>', ['coalesce', ['get', 'delaySec'], 0], 60],
                '#c98a1a',
                '#0f7a6e',
              ],
              'circle-stroke-width': 1.25,
              'circle-stroke-color': '#10242b',
              'circle-opacity': 0.92,
            }}
          />
          <Layer
            id={LABEL_LAYER_ID}
            type="symbol"
            minzoom={14}
            layout={{
              'text-field': ['coalesce', ['get', 'routeId'], ''],
              'text-size': 10,
              'text-font': ['Noto Sans Bold', 'Arial Unicode MS Bold'],
              'text-offset': [0, 1.15],
              'text-anchor': 'top',
              'text-allow-overlap': false,
            }}
            paint={{
              'text-color': '#10242b',
              'text-halo-color': 'rgba(231, 238, 241, 0.92)',
              'text-halo-width': 1.2,
            }}
          />
        </Source>
      </Map>

      <header className="map-chrome">
        <p className="brand">Transit Pulse</p>
        <p className="tagline">{tagline}</p>
        <p className={`status status-${status}`} aria-live="polite">
          {statusLabel(status, vehicleCount, error, stopsQuery)}
        </p>
      </header>

      {selection?.kind === 'stop' ? (
        <StopPanel
          feedId={feedId}
          stop={selection.stop}
          onClose={() => setSelection(null)}
          onOpenRoute={openRoute}
        />
      ) : null}

      {selection?.kind === 'route' ? (
        <RoutePanel
          feedId={feedId}
          routeId={selection.routeId}
          vehicleCount={vehicleCount}
          onClose={() => setSelection(null)}
        />
      ) : null}

      <aside className="map-legend" aria-label="Vehicle delay colours">
        <p className="map-legend-title">Delay</p>
        <ul className="map-legend-list">
          {DELAY_LEGEND.map((item) => (
            <li key={item.label}>
              <span className="map-legend-swatch" style={{ background: item.color }} aria-hidden />
              <span>{item.label}</span>
            </li>
          ))}
        </ul>
      </aside>

      {benchEnabled ? (
        <aside className="latency-bench" aria-label="Source to map latency">
          <p className="map-legend-title">Source → map</p>
          <p className="latency-bench-line">
            total n={latencyStats.total.count}
            {' · '}
            p50={formatLatencyMs(latencyStats.total.p50Ms)}
            {' · '}
            p95={formatLatencyMs(latencyStats.total.p95Ms)}
          </p>
          <p className="latency-bench-line">
            to Redis n={latencyStats.toRedis.count}
            {' · '}
            p50={formatLatencyMs(latencyStats.toRedis.p50Ms)}
            {' · '}
            p95={formatLatencyMs(latencyStats.toRedis.p95Ms)}
          </p>
          <p className="latency-bench-line">
            Redis→send n={latencyStats.redisToSend.count}
            {' · '}
            p50={formatLatencyMs(latencyStats.redisToSend.p50Ms)}
            {' · '}
            p95={formatLatencyMs(latencyStats.redisToSend.p95Ms)}
          </p>
          <p className="latency-bench-line">
            send→browser n={latencyStats.sendToBrowser.count}
            {' · '}
            p50={formatLatencyMs(latencyStats.sendToBrowser.p50Ms)}
            {' · '}
            p95={formatLatencyMs(latencyStats.sendToBrowser.p95Ms)}
          </p>
        </aside>
      ) : null}

      <footer className="map-attribution">
        <span>© HSL (CC BY 4.0) · map © OpenFreeMap · © OpenStreetMap contributors</span>
      </footer>
    </div>
  )
}

function formatLatencyMs(value: number | null): string {
  if (value === null) {
    return '—'
  }
  if (value < 1000) {
    return `${Math.round(value)} ms`
  }
  return `${(value / 1000).toFixed(2)} s`
}

function boundsFromMap(map: {
  getBounds(): { getWest(): number; getSouth(): number; getEast(): number; getNorth(): number }
}): GeoBoundingBox {
  const b = map.getBounds()
  return {
    west: b.getWest(),
    south: b.getSouth(),
    east: b.getEast(),
    north: b.getNorth(),
  }
}

function vehiclesToGeoJson(vehicles: ReadonlyMap<string, MapVehicle>): VehicleFeatureCollection {
  const features: VehicleFeatureCollection['features'] = []
  for (const vehicle of vehicles.values()) {
    features.push({
      type: 'Feature',
      id: vehicle.vehicleId,
      properties: {
        vehicleId: vehicle.vehicleId,
        routeId: vehicle.routeId,
        delaySec: vehicle.delaySec,
        bearing: vehicle.bearing,
      },
      geometry: {
        type: 'Point',
        coordinates: [vehicle.lon, vehicle.lat],
      },
    })
  }
  return { type: 'FeatureCollection', features }
}

function statusLabel(
  status: string,
  count: number,
  error: string | null,
  stopsQuery: { isPending: boolean; isError: boolean },
): string {
  if (error) {
    return error
  }
  if (stopsQuery.isError) {
    return 'Stop catalogue failed to load'
  }
  if (stopsQuery.isPending) {
    return 'Loading stops…'
  }
  switch (status) {
    case 'connecting':
      return 'Connecting…'
    case 'reconnecting':
      return 'Reconnecting…'
    case 'disconnected':
      return 'Disconnected'
    case 'connected':
      return count === 0 ? 'Listening for vehicles…' : `${count} vehicles in view`
    default:
      return status
  }
}
