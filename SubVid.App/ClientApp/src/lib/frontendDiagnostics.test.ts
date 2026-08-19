import { describe, expect, it } from 'vitest'
import { createFrontendErrorPayload } from './frontendDiagnostics'

describe('frontend diagnostics', () => {
  it('serializes only bounded error text', () => {
    const error = new Error('x'.repeat(2_000))
    error.stack = 's'.repeat(10_000)

    const payload = createFrontendErrorPayload('editor-boundary', error, 'c'.repeat(10_000))

    expect(payload.source).toBe('editor-boundary')
    expect(payload.message).toHaveLength(1_000)
    expect(payload.stack).toHaveLength(8_000)
    expect(payload.componentStack).toHaveLength(8_000)
  })

  it('normalizes unknown rejection values without serializing raw objects', () => {
    const payload = createFrontendErrorPayload('unhandled-rejection', { secret: 'hidden' })

    expect(payload.name).toBe('Error')
    expect(payload.message).toBe('Lỗi giao diện không xác định')
    expect(JSON.stringify(payload)).not.toContain('hidden')
  })
})
