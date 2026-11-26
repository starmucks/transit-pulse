import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { applyDelta } from './applyDelta'
import { clampBbox } from './clampBbox'
import type { ConnectionStatus, GeoBoundingBox, MapVehicle, MapVehicleDelta } from './types'

const HUB_PATH = '/hubs/vehicles'
const DEFAULT_FEED = 'hsl'
const SUBSCRIBE_DEBOUNCE_MS = 280

type UseVehicleHubOptions = {
  feedId?: string
  /** Latest map viewport; Subscribe is debounced on change. */
  viewport: GeoBoundingBox | null
  enabled?: boolean
}

type UseVehicleHubResult = {
  vehicles: ReadonlyMap<string, MapVehicle>
  status: ConnectionStatus
  error: string | null
  vehicleCount: number
}

/**
 * Maintains one SignalR connection to /hubs/vehicles, re-subscribes on viewport
 * change and after reconnect, and folds vehicleDelta frames into a local map.
 * Frames whose generation does not match the last Subscribe result are ignored.
 */
export function useVehicleHub({
  feedId = DEFAULT_FEED,
  viewport,
  enabled = true,
}: UseVehicleHubOptions): UseVehicleHubResult {
  const [vehicles, setVehicles] = useState<ReadonlyMap<string, MapVehicle>>(() => new Map())
  const [status, setStatus] = useState<ConnectionStatus>('disconnected')
  const [error, setError] = useState<string | null>(null)
  const connectionRef = useRef<HubConnection | null>(null)
  const viewportRef = useRef(viewport)
  viewportRef.current = viewport
  /** Generation from the last successful Subscribe; null while a Subscribe is in flight. */
  const acceptedGenerationRef = useRef<number | null>(null)

  useEffect(() => {
    if (!enabled) {
      return
    }

    let cancelled = false
    const connection = new HubConnectionBuilder()
      .withUrl(HUB_PATH)
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()

    connectionRef.current = connection

    connection.on('vehicleDelta', (delta: MapVehicleDelta) => {
      if (delta.generation !== acceptedGenerationRef.current) {
        return
      }
      setVehicles((prev) => applyDelta(prev, delta))
    })

    connection.onreconnecting(() => {
      setStatus('reconnecting')
      acceptedGenerationRef.current = null
    })

    connection.onreconnected(async () => {
      setStatus('connected')
      setError(null)
      setVehicles(new Map())
      await subscribeCurrent(connection, feedId, viewportRef.current, setError, acceptedGenerationRef)
    })

    connection.onclose(() => {
      if (!cancelled) {
        setStatus('disconnected')
        acceptedGenerationRef.current = null
      }
    })

    setStatus('connecting')
    void connection
      .start()
      .then(async () => {
        if (cancelled) {
          return
        }
        setStatus('connected')
        setError(null)
        await subscribeCurrent(connection, feedId, viewportRef.current, setError, acceptedGenerationRef)
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setStatus('disconnected')
          setError(err instanceof Error ? err.message : 'SignalR connection failed')
        }
      })

    return () => {
      cancelled = true
      connectionRef.current = null
      acceptedGenerationRef.current = null
      void (async () => {
        try {
          if (connection.state === HubConnectionState.Connected) {
            await connection.invoke('Unsubscribe')
          }
        } catch {
          // Disconnecting; ignore unsubscribe failures.
        }
        await connection.stop()
      })()
    }
  }, [enabled, feedId])

  useEffect(() => {
    if (!enabled || !viewport) {
      return
    }

    const connection = connectionRef.current
    if (!connection || connection.state !== HubConnectionState.Connected) {
      return
    }

    const handle = window.setTimeout(() => {
      // Clear markers and reject in-flight deltas until the new Subscribe generation arrives.
      acceptedGenerationRef.current = null
      setVehicles(new Map())
      void subscribeCurrent(connection, feedId, viewport, setError, acceptedGenerationRef)
    }, SUBSCRIBE_DEBOUNCE_MS)

    return () => window.clearTimeout(handle)
  }, [enabled, feedId, viewport])

  return {
    vehicles,
    status,
    error,
    vehicleCount: vehicles.size,
  }
}

async function subscribeCurrent(
  connection: HubConnection,
  feedId: string,
  viewport: GeoBoundingBox | null,
  setError: (message: string | null) => void,
  acceptedGenerationRef: { current: number | null },
): Promise<void> {
  if (!viewport) {
    return
  }

  const box = clampBbox(viewport)
  acceptedGenerationRef.current = null
  try {
    const generation = await connection.invoke<number>(
      'Subscribe',
      feedId,
      box.west,
      box.south,
      box.east,
      box.north,
      null,
    )
    acceptedGenerationRef.current = generation
    setError(null)
  } catch (err: unknown) {
    acceptedGenerationRef.current = null
    setError(err instanceof Error ? err.message : 'Subscribe failed')
  }
}
