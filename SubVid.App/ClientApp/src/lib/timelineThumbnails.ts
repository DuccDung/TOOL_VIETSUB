export const timelineThumbnailCount = 160
export const timelineThumbnailProfileVersion = 1
export const timelineThumbnailTargetWidth = 112
export const timelineThumbnailOverscan = 2

export type TimelineViewport = {
  scrollLeft: number
  clientWidth: number
  scrollWidth: number
}

export type TimelineThumbnailSample = {
  key: string
  cacheKey: string
  index: number
  time: number
  start: number
  end: number
}

export function buildTimelineThumbnailSamples(
  duration: number,
  viewport: TimelineViewport,
  sourceKey: string,
  timelineSurfaceWidth: number,
): TimelineThumbnailSample[] {
  if (!sourceKey
    || duration <= 0
    || viewport.clientWidth <= 0
    || timelineSurfaceWidth <= 0) {
    return []
  }

  const totalCellCount = Math.max(
    1,
    Math.min(timelineThumbnailCount, Math.ceil(
      timelineSurfaceWidth / timelineThumbnailTargetWidth,
    )),
  )
  const cellWidth = timelineSurfaceWidth / totalCellCount
  const cellDuration = duration / totalCellCount
  const visibleEndPixels = Math.min(
    timelineSurfaceWidth,
    viewport.scrollLeft + viewport.clientWidth,
  )
  const firstCell = Math.max(
    0,
    Math.floor(viewport.scrollLeft / cellWidth) - timelineThumbnailOverscan,
  )
  const lastCell = Math.min(
    totalCellCount,
    Math.ceil(visibleEndPixels / cellWidth) + timelineThumbnailOverscan,
  )
  const samples: TimelineThumbnailSample[] = []
  for (let cellIndex = firstCell; cellIndex < lastCell; cellIndex += 1) {
    const start = cellIndex * cellDuration
    const end = Math.min(duration, (cellIndex + 1) * cellDuration)
    if (end <= start) continue

    const midpointRatio = ((start + end) / 2) / duration
    const index = Math.min(
      timelineThumbnailCount - 1,
      Math.max(0, Math.floor(midpointRatio * timelineThumbnailCount)),
    )
    const time = duration * (index + 0.5) / timelineThumbnailCount
    samples.push({
      key: `${sourceKey}:${totalCellCount}:${cellIndex}`,
      cacheKey: `${sourceKey}:v${timelineThumbnailProfileVersion}:${index}`,
      index,
      time,
      start,
      end,
    })
  }
  return samples
}

export function prioritizeTimelineThumbnailIndices(
  samples: TimelineThumbnailSample[],
  viewportCenterTime: number,
) {
  return [...new Map(samples.map((sample) => [sample.index, sample])).values()]
    .sort((left, right) => (
      Math.abs(left.time - viewportCenterTime) - Math.abs(right.time - viewportCenterTime)
    ))
    .map((sample) => sample.index)
}
