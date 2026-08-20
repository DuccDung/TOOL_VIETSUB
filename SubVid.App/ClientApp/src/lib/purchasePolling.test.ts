import { describe, expect, it } from 'vitest'
import {
  formatVietnamDateTime,
  isPurchasePollingFinal,
  parseServerUtcTimestamp,
  remainingSecondsUntilUtc,
  shouldPollPurchase,
} from './purchasePolling'

describe('SePay purchase polling', () => {
  it('continues only while checkout is pending and no request is in flight', () => {
    expect(shouldPollPurchase(false, false)).toBe(true)
    expect(shouldPollPurchase(false, true)).toBe(false)
  })

  it('stops after paid, server expiry, or local countdown expiry', () => {
    expect(isPurchasePollingFinal(true, false, 300)).toBe(true)
    expect(isPurchasePollingFinal(false, true, 300)).toBe(true)
    expect(isPurchasePollingFinal(false, false, 0)).toBe(true)
    expect(isPurchasePollingFinal(false, false, 1)).toBe(false)
    expect(shouldPollPurchase(true, false)).toBe(false)
  })

  it('treats SQL datetime values without a timezone as UTC after polling', () => {
    const withoutZone = '2026-08-19T18:57:01.051'
    const explicitUtc = '2026-08-19T18:57:01.051Z'

    expect(parseServerUtcTimestamp(withoutZone)).toBe(parseServerUtcTimestamp(explicitUtc))
    expect(remainingSecondsUntilUtc(withoutZone, Date.parse('2026-08-19T18:42:01.051Z'))).toBe(900)
    expect(formatVietnamDateTime(withoutZone)).toContain('20/08/2026')
  })
})
