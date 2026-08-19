export type VirtualRowRange = {
  startIndex: number
  endIndex: number
  offsetPixels: number
  totalHeightPixels: number
}

export type VirtualViewport = {
  scrollTop: number
  height: number
}

export function updateVirtualViewport(
  current: VirtualViewport,
  scrollTop: number,
  height: number,
  minimumHeight: number,
): VirtualViewport {
  const nextScrollTop = Math.max(0, Number.isFinite(scrollTop) ? scrollTop : 0)
  const nextHeight = Math.max(
    Math.max(1, minimumHeight),
    Number.isFinite(height) ? height : minimumHeight,
  )
  if (
    Math.abs(current.scrollTop - nextScrollTop) < 0.5
    && Math.abs(current.height - nextHeight) < 0.5
  ) {
    return current
  }
  return { scrollTop: nextScrollTop, height: nextHeight }
}

export function getVirtualRowRange(
  itemCount: number,
  scrollTop: number,
  viewportHeight: number,
  rowHeight: number,
  overscan = 6,
): VirtualRowRange {
  const count = Math.max(0, Math.floor(itemCount))
  const height = Math.max(1, rowHeight)
  const safeScroll = Math.max(0, scrollTop)
  const safeViewport = Math.max(height, viewportHeight)
  const startIndex = Math.max(0, Math.floor(safeScroll / height) - overscan)
  const endIndex = Math.min(
    count,
    Math.ceil((safeScroll + safeViewport) / height) + overscan,
  )
  return {
    startIndex,
    endIndex,
    offsetPixels: startIndex * height,
    totalHeightPixels: count * height,
  }
}

type TimedItem = {
  id: number
  start: number
  end: number
}

export function selectVisibleTimelineItems<T extends TimedItem>(
  items: readonly T[],
  duration: number,
  scrollLeft: number,
  viewportWidth: number,
  surfaceWidth: number,
  importantIds: ReadonlySet<number>,
  maximumItems = 420,
): T[] {
  if (items.length === 0) return []
  const safeDuration = Math.max(0.001, duration)
  const safeSurface = Math.max(1, surfaceWidth)
  const visibleDuration = Math.max(
    safeDuration * Math.max(1, viewportWidth) / safeSurface,
    safeDuration / safeSurface,
  )
  const overscan = Math.max(5, visibleDuration * 0.15)
  const start = Math.max(0, safeDuration * Math.max(0, scrollLeft) / safeSurface - overscan)
  const end = Math.min(
    safeDuration,
    safeDuration * (Math.max(0, scrollLeft) + Math.max(1, viewportWidth)) / safeSurface + overscan,
  )
  const visible = items.filter(item => item.end >= start && item.start <= end)
  if (visible.length <= maximumItems) return visible

  const stride = Math.ceil(visible.length / maximumItems)
  return visible.filter((item, index) => index % stride === 0 || importantIds.has(item.id))
}
