/**
 * Source→map latency samples: wall clock when a SignalR vehicle arrives minus
 * `observedAt` (source measurement time carried through the pipeline).
 *
 * Negative samples (client clock behind the source) are dropped — they cannot
 * be a real end-to-end delay and usually mean clock skew.
 */

export type LatencyStats = {
  count: number
  p50Ms: number | null
  p95Ms: number | null
  maxMs: number | null
}

/** Keeps the most recent samples and reports percentiles. */
export class SourceToMapLatencyBuffer {
  private readonly _samples: number[] = []
  private readonly _capacity: number

  constructor(capacity = 2_000) {
    this._capacity = Math.max(1, capacity)
  }

  /** Records one sample; returns the delay in ms, or null if rejected. */
  record(observedAtIso: string, receivedAtMs = Date.now()): number | null {
    const observedMs = Date.parse(observedAtIso)
    if (Number.isNaN(observedMs)) {
      return null
    }

    return this.recordDelayMs(receivedAtMs - observedMs)
  }

  /**
   * Records a precomputed delay in ms. Negative and &gt; 120 s samples are dropped
   * (same rules as {@link record}).
   */
  recordDelayMs(delayMs: number): number | null {
    if (delayMs < 0 || delayMs > 120_000) {
      return null
    }

    this._samples.push(delayMs)
    if (this._samples.length > this._capacity) {
      this._samples.shift()
    }
    return delayMs
  }

  get size(): number {
    return this._samples.length
  }

  stats(): LatencyStats {
    if (this._samples.length === 0) {
      return { count: 0, p50Ms: null, p95Ms: null, maxMs: null }
    }

    const sorted = [...this._samples].sort((a, b) => a - b)
    return {
      count: sorted.length,
      p50Ms: percentile(sorted, 0.5),
      p95Ms: percentile(sorted, 0.95),
      maxMs: sorted[sorted.length - 1] ?? null,
    }
  }

  clear(): void {
    this._samples.length = 0
  }
}

function percentile(sortedAscending: readonly number[], p: number): number {
  if (sortedAscending.length === 1) {
    return sortedAscending[0]!
  }
  const index = (sortedAscending.length - 1) * p
  const lower = Math.floor(index)
  const upper = Math.ceil(index)
  if (lower === upper) {
    return sortedAscending[lower]!
  }
  const weight = index - lower
  return sortedAscending[lower]! * (1 - weight) + sortedAscending[upper]! * weight
}
