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
  /** Latest map viewport; used when not filtering by route. */
  viewport: GeoBoundingBox | null
  /** When set, Subscribe filters to this route (city covering box on the server). */
  routeId?: string | null
  enabled?: boolean
  /** Called for deltas that pass the generation filter (stage 3.8 latency bench). */
  onAcceptedDelta?: (delta: MapVehicleDelta) => void
}

type UseVehicleHubResult = {
  vehicles: ReadonlyMap<string, MapVehicle>
  status: ConnectionStatus
  error: string | null
  vehicleCount: number
}

type SubscribeEpoch = { current: number }

/**
 * Maintains one SignalR connection to /hubs/vehicles, re-subscribes on viewport
 * or route filter change and after reconnect, and folds vehicleDelta frames into
 * a local map. Frames whose generation does not match the last Subscribe result
 * are ignored. Pan/zoom does not re-subscribe while a route filter is active.
 */
export function useVehicleHub({
  feedId = DEFAULT_FEED,
  viewport,
  routeId = null,
  enabled = true,
  onAcceptedDelta,
}: UseVehicleHubOptions): UseVehicleHubResult {
  const [vehicles, setVehicles] = useState<ReadonlyMap<string, MapVehicle>>(() => new Map())
  const [status, setStatus] = useState<ConnectionStatus>('disconnected')
  const [error, setError] = useState<string | null>(null)
  const connectionRef = useRef<HubConnection | null>(null)
  const viewportRef = useRef(viewport)
  viewportRef.current = viewport
  const routeIdRef = useRef(routeId)
  routeIdRef.current = routeId
  const onAcceptedDeltaRef = useRef(onAcceptedDelta)
  onAcceptedDeltaRef.current = onAcceptedDelta
  /** Generation from the last successful Subscribe; null while a Subscribe is in flight. */
  const acceptedGenerationRef = useRef<number | null>(null)
  /** Bumps on every Subscribe attempt so a stale invoke cannot win. */
  const subscribeEpochRef = useRef(0)

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
      onAcceptedDeltaRef.current?.(delta)
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
      await subscribeCurrent(
        connection,
        feedId,
        viewportRef.current,
        routeIdRef.current,
        setError,
        acceptedGenerationRef,
        subscribeEpochRef,
      )
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
        await subscribeCurrent(
          connection,
          feedId,
          viewportRef.current,
          routeIdRef.current,
          setError,
          acceptedGenerationRef,
          subscribeEpochRef,
        )
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
      subscribeEpochRef.current += 1
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

  // City mode: follow the viewport. Skip while a route filter owns the subscription.
  useEffect(() => {
    if (!enabled || routeId || !viewport) {
      return
    }

    const connection = connectionRef.current
    if (!connection || connection.state !== HubConnectionState.Connected) {
      return
    }

    const handle = window.setTimeout(() => {
      acceptedGenerationRef.current = null
      setVehicles(new Map())
      void subscribeCurrent(
        connection,
        feedId,
        viewport,
        null,
        setError,
        acceptedGenerationRef,
        subscribeEpochRef,
      )
    }, SUBSCRIBE_DEBOUNCE_MS)

    return () => window.clearTimeout(handle)
  }, [enabled, feedId, viewport, routeId])

  // Route mode: city covering box on the server; ignore pan/zoom.
  useEffect(() => {
    if (!enabled || !routeId) {
      return
    }

    const connection = connectionRef.current
    if (!connection || connection.state !== HubConnectionState.Connected) {
      return
    }

    const handle = window.setTimeout(() => {
      acceptedGenerationRef.current = null
      setVehicles(new Map())
      void subscribeCurrent(
        connection,
        feedId,
        null,
        routeId,
        setError,
        acceptedGenerationRef,
        subscribeEpochRef,
      )
    }, SUBSCRIBE_DEBOUNCE_MS)

    return () => window.clearTimeout(handle)
  }, [enabled, feedId, routeId])

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
  routeId: string | null | undefined,
  setError: (message: string | null) => void,
  acceptedGenerationRef: { current: number | null },
  subscribeEpochRef: SubscribeEpoch,
): Promise<void> {
  const filteredRoute = routeId?.trim() ? routeId.trim() : null

  if (!filteredRoute && !viewport) {
    return
  }

  const epoch = ++subscribeEpochRef.current
  acceptedGenerationRef.current = null
  try {
    let generation: number
    if (filteredRoute) {
      generation = await connection.invoke<number>(
        'Subscribe',
        feedId,
        null,
        null,
        null,
        null,
        filteredRoute,
      )
    } else {
      const box = clampBbox(viewport!)
      generation = await connection.invoke<number>(
        'Subscribe',
        feedId,
        box.west,
        box.south,
        box.east,
        box.north,
        null,
      )
    }

    if (epoch !== subscribeEpochRef.current) {
      return
    }

    acceptedGenerationRef.current = generation
    setError(null)
  } catch (err: unknown) {
    if (epoch !== subscribeEpochRef.current) {
      return
    }
    acceptedGenerationRef.current = null
    setError(err instanceof Error ? err.message : 'Subscribe failed')
  }
}
