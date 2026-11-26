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
      setVehicles((prev) => applyDelta(prev, delta))
    })

    connection.onreconnecting(() => {
      setStatus('reconnecting')
    })

    connection.onreconnected(async () => {
      setStatus('connected')
      setError(null)
      setVehicles(new Map())
      await subscribeCurrent(connection, feedId, viewportRef.current, setError)
    })

    connection.onclose(() => {
      if (!cancelled) {
        setStatus('disconnected')
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
        await subscribeCurrent(connection, feedId, viewportRef.current, setError)
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
      // Subscribe resets server last-sent; clear local markers so vehicles that left the
      // new box are not stale until the first delta arrives as "added".
      setVehicles(new Map())
      void subscribeCurrent(connection, feedId, viewport, setError)
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
): Promise<void> {
  if (!viewport) {
    return
  }

  const box = clampBbox(viewport)
  try {
    await connection.invoke(
      'Subscribe',
      feedId,
      box.west,
      box.south,
      box.east,
      box.north,
      null,
    )
    setError(null)
  } catch (err: unknown) {
    setError(err instanceof Error ? err.message : 'Subscribe failed')
  }
}
