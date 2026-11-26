/** Formats an ISO instant as local HH:MM:SS for the stop panel. */
export function formatClock(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) {
    return '—'
  }
  return date.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

/** Formats schedule delay for the panel (null → unknown). */
export function formatDelay(delaySec: number | null): string {
  if (delaySec === null) {
    return 'delay unknown'
  }
  if (delaySec === 0) {
    return 'on time'
  }
  // Sub-minute values would round to "+0 mins" — show seconds instead.
  if (Math.abs(delaySec) < 60) {
    return delaySec > 0 ? `+${delaySec} s` : `−${Math.abs(delaySec)} s`
  }
  const minutes = Math.round(Math.abs(delaySec) / 60)
  const unit = minutes === 1 ? 'min' : 'mins'
  return delaySec > 0 ? `+${minutes} ${unit}` : `−${minutes} ${unit}`
}

/** On-time share as a percentage string, or em dash when there are no arrivals. */
export function formatOnTimeShare(onTime: number, arrivals: number): string {
  if (arrivals <= 0) {
    return '—'
  }
  return `${Math.round((onTime / arrivals) * 100)}%`
}

/** Average delay for the punctuality panel. */
export function formatAvgDelay(avgSec: number | null): string {
  if (avgSec === null) {
    return '—'
  }
  return formatDelay(Math.round(avgSec))
}
