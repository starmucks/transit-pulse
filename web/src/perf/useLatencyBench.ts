import { useEffect, useRef, useState } from 'react'
import type { MapVehicleDelta } from '../map/types'
import { noteLatencyDelta, type SegmentLatencyBuffers } from './noteLatencyDelta'
import { SourceToMapLatencyBuffer, type LatencyStats } from './sourceToMapLatency'

const empty: LatencyStats = { count: 0, p50Ms: null, p95Ms: null, maxMs: null }

export type SegmentLatencyStats = {
  total: LatencyStats
  toRedis: LatencyStats
  redisToSend: LatencyStats
  sendToBrowser: LatencyStats
}

const emptySegments: SegmentLatencyStats = {
  total: empty,
  toRedis: empty,
  redisToSend: empty,
  sendToBrowser: empty,
}

/** True when the page was opened with `?bench=1` (stage 3.8 measurement UI). */
export function isLatencyBenchEnabled(): boolean {
  if (typeof window === 'undefined') {
    return false
  }
  return new URLSearchParams(window.location.search).get('bench') === '1'
}

function createBuffers(): SegmentLatencyBuffers {
  return {
    total: new SourceToMapLatencyBuffer(),
    toRedis: new SourceToMapLatencyBuffer(),
    redisToSend: new SourceToMapLatencyBuffer(),
    sendToBrowser: new SourceToMapLatencyBuffer(),
  }
}

/**
 * Collects source→map latency (and C3 segment ages) from accepted vehicle deltas
 * and refreshes percentile stats about once a second while `enabled`.
 *
 * Only a new `observedAt` per vehicle counts — the SignalR ticker would otherwise
 * inflate the delay by replaying the same observation every tick.
 */
export function useLatencyBench(enabled: boolean): {
  stats: SegmentLatencyStats
  noteSample: (delta: MapVehicleDelta) => void
} {
  const buffersRef = useRef(createBuffers())
  const lastObservedRef = useRef(new Map<string, string>())
  const [stats, setStats] = useState<SegmentLatencyStats>(emptySegments)

  useEffect(() => {
    if (!enabled) {
      return
    }
    const handle = window.setInterval(() => {
      const b = buffersRef.current
      setStats({
        total: b.total.stats(),
        toRedis: b.toRedis.stats(),
        redisToSend: b.redisToSend.stats(),
        sendToBrowser: b.sendToBrowser.stats(),
      })
    }, 1_000)
    return () => window.clearInterval(handle)
  }, [enabled])

  const noteSample = (delta: MapVehicleDelta) => {
    if (!enabled) {
      return
    }
    noteLatencyDelta(buffersRef.current, lastObservedRef.current, delta, Date.now())
  }

  return { stats, noteSample }
}
