import { describe, expect, it } from 'vitest'
import {
  buildTimelineThumbnailSamples,
  prioritizeTimelineThumbnailIndices,
  timelineThumbnailCount,
} from './timelineThumbnails'

describe('timeline thumbnails', () => {
  it('virtualizes samples to the visible viewport and overscan', () => {
    const samples = buildTimelineThumbnailSamples(
      100,
      { scrollLeft: 1_000, clientWidth: 1_000, scrollWidth: 4_000 },
      'source',
      4_000,
    )

    expect(samples.length).toBeLessThan(16)
    expect(samples.every((sample) => sample.start < 60 && sample.end > 15)).toBe(true)
  })

  it('maps every zoom density onto one stable 160-frame cache grid', () => {
    const samples = buildTimelineThumbnailSamples(
      320,
      { scrollLeft: 0, clientWidth: 1_200, scrollWidth: 20_000 },
      'source',
      20_000,
    )

    expect(samples.every((sample) => (
      sample.index >= 0
      && sample.index < timelineThumbnailCount
      && sample.cacheKey === `source:v1:${sample.index}`
      && sample.time === 320 * (sample.index + 0.5) / timelineThumbnailCount
    ))).toBe(true)
  })

  it('prioritizes the frames nearest the viewport center without duplicates', () => {
    const samples = buildTimelineThumbnailSamples(
      100,
      { scrollLeft: 0, clientWidth: 1_000, scrollWidth: 2_000 },
      'source',
      2_000,
    )
    const indices = prioritizeTimelineThumbnailIndices(samples, 25)

    expect(new Set(indices).size).toBe(indices.length)
    expect(Math.abs(samples.find((sample) => sample.index === indices[0])!.time - 25))
      .toBeLessThanOrEqual(Math.abs(samples.find((sample) => sample.index === indices.at(-1))!.time - 25))
  })

  it('returns no work before the viewport has a measurable width', () => {
    expect(buildTimelineThumbnailSamples(
      100,
      { scrollLeft: 0, clientWidth: 0, scrollWidth: 0 },
      'source',
      1_000,
    )).toEqual([])
  })
})
