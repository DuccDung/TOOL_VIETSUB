import type { SubtitleRemovalRegion, SubtitleRemovalSettings } from '../types'

export const maxSubtitleRemovalRegions = 10

export const defaultSubtitleRemovalRegion: SubtitleRemovalRegion = {
  id: 'primary',
  x: 0.05,
  y: 0.70,
  width: 0.90,
  height: 0.16,
}

function createRegionId() {
  if (typeof globalThis.crypto?.randomUUID === 'function') {
    return globalThis.crypto.randomUUID()
  }

  return `mask-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`
}

export function getSubtitleRemovalRegions(settings: SubtitleRemovalSettings): SubtitleRemovalRegion[] {
  if (settings.regions.length > 0) return settings.regions

  return [{
    id: 'legacy',
    x: settings.x,
    y: settings.y,
    width: settings.width,
    height: settings.height,
  }]
}

export function withSubtitleRemovalRegions(
  settings: SubtitleRemovalSettings,
  regions: SubtitleRemovalRegion[],
): SubtitleRemovalSettings {
  const normalizedRegions = regions.length > 0
    ? regions
    : [{ ...defaultSubtitleRemovalRegion, id: createRegionId() }]
  const primary = normalizedRegions[0]
  return {
    ...settings,
    x: primary.x,
    y: primary.y,
    width: primary.width,
    height: primary.height,
    regions: normalizedRegions,
  }
}

export function createSubtitleRemovalRegion(existingCount: number): SubtitleRemovalRegion {
  const width = 0.42
  const height = 0.12
  const cascade = Math.max(0, existingCount - 1)
  return {
    id: createRegionId(),
    x: Math.min(1 - width, 0.25 + (cascade % 4) * 0.055),
    y: Math.min(1 - height, 0.18 + (cascade % 6) * 0.105),
    width,
    height,
  }
}
