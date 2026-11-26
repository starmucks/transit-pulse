import { describe, expect, it } from 'vitest'
import { clampBbox, MAX_SPAN_DEGREES } from './clampBbox'

describe('clampBbox', () => {
  it('keeps a small box unchanged', () => {
    const box = { west: 24.9, south: 60.1, east: 25.0, north: 60.2 }
    const clamped = clampBbox(box)
    expect(clamped.west).toBeCloseTo(box.west, 10)
    expect(clamped.east).toBeCloseTo(box.east, 10)
    expect(clamped.south).toBeCloseTo(box.south, 10)
    expect(clamped.north).toBeCloseTo(box.north, 10)
  })

  it('shrinks a wide box around its centre', () => {
    const box = { west: 24.0, south: 59.5, east: 26.0, north: 61.0 }
    const clamped = clampBbox(box)

    expect(clamped.east - clamped.west).toBeCloseTo(MAX_SPAN_DEGREES, 10)
    expect(clamped.north - clamped.south).toBeCloseTo(MAX_SPAN_DEGREES, 10)
    expect((clamped.west + clamped.east) / 2).toBeCloseTo(25.0, 10)
    expect((clamped.south + clamped.north) / 2).toBeCloseTo(60.25, 10)
  })
})
