import { describe, expect, it } from 'vitest'
import { SourceToMapLatencyBuffer } from './sourceToMapLatency'

describe('SourceToMapLatencyBuffer', () => {
  it('computes p50/p95 from accepted samples', () => {
    const buffer = new SourceToMapLatencyBuffer()
    const base = Date.parse('2026-11-26T18:00:00.000Z')
    for (let i = 1; i <= 20; i++) {
      const observed = new Date(base).toISOString()
      buffer.record(observed, base + i * 100)
    }

    const stats = buffer.stats()
    expect(stats.count).toBe(20)
    expect(stats.p50Ms).toBeGreaterThan(900)
    expect(stats.p50Ms).toBeLessThan(1_200)
    expect(stats.p95Ms).toBeGreaterThan(1_800)
    expect(stats.maxMs).toBe(2_000)
  })

  it('drops negative and absurd delays', () => {
    const buffer = new SourceToMapLatencyBuffer()
    const now = Date.parse('2026-11-26T18:00:00.000Z')
    expect(buffer.record(new Date(now + 5_000).toISOString(), now)).toBeNull()
    expect(buffer.record(new Date(now - 200_000).toISOString(), now)).toBeNull()
    expect(buffer.recordDelayMs(-1)).toBeNull()
    expect(buffer.recordDelayMs(200_000)).toBeNull()
    expect(buffer.size).toBe(0)
  })
})
