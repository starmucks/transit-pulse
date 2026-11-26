import { useEffect, useRef, useState } from 'react'
import type { MapVehicleDelta } from '../map/types'
import { noteLatencyDelta } from './noteLatencyDelta'
import { SourceToMapLatencyBuffer, type LatencyStats } from './sourceToMapLatency'

const empty: LatencyStats = { count: 0, p50Ms: null, p95Ms: null, maxMs: null }

/** True when the page was opened with `?bench=1` (stage 3.8 measurement UI). */
export function isLatencyBenchEnabled(): boolean {
  if (typeof window === 'undefined') {
    return false
  }
  return new URLSearchParams(window.location.search).get('bench') === '1'
}

/**
 * Collects source→map latency from accepted vehicle deltas and refreshes
 * percentile stats about once a second while `enabled`.
 *
 * Only a new `observedAt` per vehicle counts — the 1 Hz SignalR ticker would
 * otherwise inflate the delay by replaying the same observation every second.
 */
export function useLatencyBench(enabled: boolean): {
  stats: LatencyStats
  noteSample: (delta: MapVehicleDelta) => void
} {
  const bufferRef = useRef(new SourceToMapLatencyBuffer())
  const lastObservedRef = useRef(new Map<string, string>())
  const [stats, setStats] = useState<LatencyStats>(empty)

  useEffect(() => {
    if (!enabled) {
      return
    }
    const handle = window.setInterval(() => {
      setStats(bufferRef.current.stats())
    }, 1_000)
    return () => window.clearInterval(handle)
  }, [enabled])

  const noteSample = (delta: MapVehicleDelta) => {
    if (!enabled) {
      return
    }
    noteLatencyDelta(bufferRef.current, lastObservedRef.current, delta, Date.now())
  }

  return { stats, noteSample }
}
