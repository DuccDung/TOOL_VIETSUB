export type TimelineFollowGeometry = {
  time: number
  scrollLeft: number
  playheadPixels: number
}

/**
 * Positions a playhead in three phases:
 * 1. the playhead travels from the viewport start to its preferred anchor;
 * 2. it stays at that anchor while timeline content scrolls underneath it;
 * 3. after content reaches its maximum scroll, it travels to the viewport end.
 */
export function getTimelineFollowGeometry(
  requestedTime: number,
  duration: number,
  timelineSurfaceWidth: number,
  viewportWidth: number,
  anchorRatio: number,
): TimelineFollowGeometry {
  const safeDuration = Math.max(0.001, Number.isFinite(duration) ? duration : 0.001)
  const safeSurfaceWidth = Math.max(0, Number.isFinite(timelineSurfaceWidth) ? timelineSurfaceWidth : 0)
  const safeViewportWidth = Math.max(0, Number.isFinite(viewportWidth) ? viewportWidth : 0)
  const safeTime = Number.isFinite(requestedTime) ? requestedTime : 0
  const time = Math.min(safeDuration, Math.max(0, safeTime))
  const timePixels = time / safeDuration * safeSurfaceWidth
  const normalizedAnchor = Math.min(1, Math.max(0, Number.isFinite(anchorRatio) ? anchorRatio : 0.5))
  const anchorPixels = safeViewportWidth * normalizedAnchor
  const maximumScrollLeft = Math.max(0, safeSurfaceWidth - safeViewportWidth)
  const scrollLeft = Math.min(
    maximumScrollLeft,
    Math.max(0, timePixels - anchorPixels),
  )

  return {
    time,
    scrollLeft,
    playheadPixels: Math.min(
      safeViewportWidth,
      Math.max(0, timePixels - scrollLeft),
    ),
  }
}
