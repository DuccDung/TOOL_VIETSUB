import { describe, expect, it } from 'vitest'
import {
  getSubtitleRemovalRegions,
  withSubtitleRemovalRegions,
} from './subtitleRemoval'
import type { SubtitleRemovalSettings } from '../types'

const settings: SubtitleRemovalSettings = {
  enabled: true,
  mode: 'blur',
  x: 0.05,
  y: 0.70,
  width: 0.90,
  height: 0.16,
  regions: [{ id: 'primary', x: 0.05, y: 0.70, width: 0.90, height: 0.16 }],
}

describe('subtitle removal regions', () => {
  it('keeps an intentionally empty region list empty after the final region is deleted', () => {
    const next = withSubtitleRemovalRegions({ ...settings, enabled: false }, [])

    expect(next.enabled).toBe(false)
    expect(getSubtitleRemovalRegions(next)).toEqual([])
    expect(next.x).toBe(settings.x)
    expect(next.height).toBe(settings.height)
  })
})
