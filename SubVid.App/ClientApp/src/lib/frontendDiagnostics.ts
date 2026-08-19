import { postToHost } from './host'

export type FrontendErrorPayload = {
  source: string
  name: string
  message: string
  stack: string
  componentStack: string
}

const limits = {
  source: 80,
  name: 120,
  message: 1_000,
  stack: 8_000,
  componentStack: 8_000,
}

let lastSignature = ''
let lastReportedAt = 0

function safeText(value: unknown, maximumLength: number) {
  const text = typeof value === 'string' ? value : ''
  return text.slice(0, maximumLength)
}

export function createFrontendErrorPayload(
  source: string,
  error: unknown,
  componentStack = '',
): FrontendErrorPayload {
  const errorLike = error instanceof Error
    ? error
    : new Error(typeof error === 'string' ? error : 'Lỗi giao diện không xác định')
  return {
    source: safeText(source, limits.source) || 'unknown',
    name: safeText(errorLike.name, limits.name) || 'Error',
    message: safeText(errorLike.message, limits.message),
    stack: safeText(errorLike.stack, limits.stack),
    componentStack: safeText(componentStack, limits.componentStack),
  }
}

export function reportFrontendError(source: string, error: unknown, componentStack = '') {
  const payload = createFrontendErrorPayload(source, error, componentStack)
  const signature = `${payload.source}:${payload.name}:${payload.message}:${payload.stack.slice(0, 300)}`
  const now = Date.now()
  if (signature === lastSignature && now - lastReportedAt < 1_000) return
  lastSignature = signature
  lastReportedAt = now
  postToHost('ui:error', payload)
}
