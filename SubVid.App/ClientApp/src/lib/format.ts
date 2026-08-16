export function formatClock(totalSeconds: number | null | undefined, showCentiseconds = false) {
  const numericSeconds = Number(totalSeconds)
  const safeSeconds = Number.isFinite(numericSeconds) ? Math.max(0, numericSeconds) : 0
  const hours = Math.floor(safeSeconds / 3600)
  const minutes = Math.floor((safeSeconds % 3600) / 60)
  const seconds = Math.floor(safeSeconds % 60)
  const centiseconds = Math.floor((safeSeconds % 1) * 100)
  const base = [hours, minutes, seconds]
    .map((part) => part.toString().padStart(2, '0'))
    .join(':')

  return showCentiseconds
    ? `${base}.${centiseconds.toString().padStart(2, '0')}`
    : base
}

export function formatBytes(bytes: number) {
  if (bytes <= 0) return '0 MB'
  const megabytes = bytes / 1024 / 1024
  return `${megabytes.toFixed(megabytes >= 100 ? 0 : 1)} MB`
}
