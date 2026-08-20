const explicitTimezonePattern = /(?:z|[+-]\d{2}:?\d{2})$/i

export function parseServerUtcTimestamp(value: string) {
  const trimmed = value.trim()
  if (!trimmed) return Number.NaN
  return Date.parse(explicitTimezonePattern.test(trimmed) ? trimmed : `${trimmed}Z`)
}

export function remainingSecondsUntilUtc(value: string, nowMilliseconds = Date.now()) {
  const expiresAtMilliseconds = parseServerUtcTimestamp(value)
  if (!Number.isFinite(expiresAtMilliseconds)) return 0
  return Math.max(0, Math.ceil((expiresAtMilliseconds - nowMilliseconds) / 1000))
}

export function formatVietnamDateTime(value: string) {
  const milliseconds = parseServerUtcTimestamp(value)
  if (!Number.isFinite(milliseconds)) return 'Không xác định'
  return new Intl.DateTimeFormat('vi-VN', {
    timeZone: 'Asia/Ho_Chi_Minh',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(milliseconds))
}

export function isPurchasePollingFinal(
  isPaid: boolean,
  isExpired: boolean,
  remainingSeconds: number,
) {
  return isPaid || isExpired || remainingSeconds <= 0
}

export function shouldPollPurchase(final: boolean, requestPending: boolean) {
  return !final && !requestPending
}
