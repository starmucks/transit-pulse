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
const SOURCE_ID = 'vehicles'
const CIRCLE_LAYER_ID = 'vehicles-circle'
const LABEL_LAYER_ID = 'vehicles-label'

type LiveMapProps = {
  feedId?: string
}

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
 * Full-bleed Helsinki map: viewport-following SignalR subscription, circle markers
 * for live vehicles, brand chrome and data-source attribution.
 */
export function LiveMap({ feedId = 'hsl' }: LiveMapProps) {
  const mapRef = useRef<MapRef>(null)
  const [viewport, setViewport] = useState<GeoBoundingBox | null>(HELSINKI_VIEWPORT)
  const { vehicles, status, error, vehicleCount } = useVehicleHub({ feedId, viewport })

  const geojson = useMemo(() => vehiclesToGeoJson(vehicles), [vehicles])

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

  const onClick = useCallback((_event: MapLayerMouseEvent) => {
    // Stage 3.7: stop / route panel from clicked feature.
  }, [])

  return (
    <div className="live-map">
      <Map
        ref={mapRef}
        initialViewState={HELSINKI}
        mapStyle={STYLE_URL}
        attributionControl={false}
        interactiveLayerIds={[CIRCLE_LAYER_ID]}
        onLoad={onLoad}
        onMoveEnd={onMoveEnd}
        onClick={onClick}
        style={{ width: '100%', height: '100%' }}
      >
        <NavigationControl position="bottom-right" showCompass={false} />
        <Source id={SOURCE_ID} type="geojson" data={geojson}>
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
        <p className="tagline">Live Helsinki · HSL positions over SignalR</p>
        <p className={`status status-${status}`} aria-live="polite">
          {statusLabel(status, vehicleCount, error)}
        </p>
      </header>

      <footer className="map-attribution">
        <span>© HSL (CC BY 4.0) · map © OpenFreeMap · © OpenStreetMap contributors</span>
      </footer>
    </div>
  )
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

function statusLabel(status: string, count: number, error: string | null): string {
  if (error) {
    return error
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
