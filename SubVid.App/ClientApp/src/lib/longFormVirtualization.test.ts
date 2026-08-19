import { describe, expect, it } from 'vitest'
import {
  getVirtualRowRange,
  selectVisibleTimelineItems,
  updateVirtualViewport,
} from './longFormVirtualization'

describe('long-form virtualization', () => {
  it('keeps the subtitle DOM window bounded', () => {
    const range = getVirtualRowRange(10_000, 76 * 5_000, 760, 76)

    expect(range.startIndex).toBe(4_994)
    expect(range.endIndex).toBe(5_016)
    expect(range.totalHeightPixels).toBe(760_000)
  })

  it.each([5, 260, 1_000, 10_000])('bounds the rendered window for %i cues', (itemCount) => {
    const range = getVirtualRowRange(itemCount, itemCount * 38, 760, 76)

    expect(range.startIndex).toBeGreaterThanOrEqual(0)
    expect(range.endIndex).toBeLessThanOrEqual(itemCount)
    expect(range.endIndex - range.startIndex).toBeLessThanOrEqual(22)
  })

  it('does not schedule a React update for an unchanged viewport measurement', () => {
    const current = { scrollTop: 120, height: 600 }

    expect(updateVirtualViewport(current, 120.2, 600.2, 76)).toBe(current)
    expect(updateVirtualViewport(current, Number.NaN, 10, 76)).toEqual({
      scrollTop: 0,
      height: 76,
    })
  })

  it('limits dense timeline items while retaining selected items', () => {
    const items = Array.from({ length: 5_000 }, (_, id) => ({
      id,
      start: id,
      end: id + 0.8,
    }))
    const selected = 2_499
    const visible = selectVisibleTimelineItems(
      items,
      5_000,
      0,
      1_000,
      2_000,
      new Set([selected]),
      200,
    )

    expect(visible.length).toBeLessThanOrEqual(201)
    expect(visible.some(item => item.id === selected)).toBe(true)
    expect(visible.every(item => item.start <= 2_875)).toBe(true)
  })
})
