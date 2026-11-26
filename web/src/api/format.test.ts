import { describe, expect, it } from 'vitest'
import { formatAvgDelay, formatDelay, formatOnTimeShare } from './format'

describe('formatDelay', () => {
  it('labels unknown, on-time, seconds and signed minutes', () => {
    expect(formatDelay(null)).toBe('delay unknown')
    expect(formatDelay(0)).toBe('on time')
    expect(formatDelay(20)).toBe('+20 s')
    expect(formatDelay(-45)).toBe('−45 s')
    expect(formatDelay(125)).toBe('+2 mins')
    expect(formatDelay(-60)).toBe('−1 min')
  })
})

describe('formatOnTimeShare', () => {
  it('returns em dash when there are no arrivals', () => {
    expect(formatOnTimeShare(0, 0)).toBe('—')
  })

  it('rounds the share', () => {
    expect(formatOnTimeShare(1, 3)).toBe('33%')
  })
})

describe('formatAvgDelay', () => {
  it('delegates to formatDelay for rounded seconds', () => {
    expect(formatAvgDelay(null)).toBe('—')
    expect(formatAvgDelay(90)).toBe('+2 mins')
  })
})
