import type { SubtitleRemovalRegion, SubtitleRemovalSettings } from '../types'

export const maxSubtitleRemovalRegions = 10

function createRegionId() {
  if (typeof globalThis.crypto?.randomUUID === 'function') {
    return globalThis.crypto.randomUUID()
  }

  return `mask-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`
}

export function getSubtitleRemovalRegions(settings: SubtitleRemovalSettings): SubtitleRemovalRegion[] {
  return settings.regions
}

export function withSubtitleRemovalRegions(
  settings: SubtitleRemovalSettings,
  regions: SubtitleRemovalRegion[],
): SubtitleRemovalSettings {
  const primary = regions[0]
  return {
    ...settings,
    x: primary?.x ?? settings.x,
    y: primary?.y ?? settings.y,
    width: primary?.width ?? settings.width,
    height: primary?.height ?? settings.height,
    regions,
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
