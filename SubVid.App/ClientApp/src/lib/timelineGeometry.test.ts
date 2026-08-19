import { describe, expect, it } from 'vitest'
import { getTimelineFollowGeometry } from './timelineGeometry'

describe('getTimelineFollowGeometry', () => {
  const duration = 100
  const surfaceWidth = 2_000
  const viewportWidth = 1_000

  it('starts at the beginning of both the media and viewport', () => {
    expect(getTimelineFollowGeometry(0, duration, surfaceWidth, viewportWidth, 0.5)).toEqual({
      time: 0,
      scrollLeft: 0,
      playheadPixels: 0,
    })
  })

  it('moves the playhead before it reaches the preferred anchor', () => {
    expect(getTimelineFollowGeometry(10, duration, surfaceWidth, viewportWidth, 0.5)).toEqual({
      time: 10,
      scrollLeft: 0,
      playheadPixels: 200,
    })
  })

  it('holds the playhead at the anchor while the timeline can scroll', () => {
    expect(getTimelineFollowGeometry(50, duration, surfaceWidth, viewportWidth, 0.5)).toEqual({
      time: 50,
      scrollLeft: 500,
      playheadPixels: 500,
    })
  })

  it('moves the playhead toward the end after scrolling is exhausted', () => {
    expect(getTimelineFollowGeometry(90, duration, surfaceWidth, viewportWidth, 0.5)).toEqual({
      time: 90,
      scrollLeft: 1_000,
      playheadPixels: 800,
    })
  })

  it('ends at the final timeline pixel', () => {
    expect(getTimelineFollowGeometry(100, duration, surfaceWidth, viewportWidth, 0.5)).toEqual({
      time: 100,
      scrollLeft: 1_000,
      playheadPixels: 1_000,
    })
  })

  it('respects a user-selected anchor', () => {
    expect(getTimelineFollowGeometry(40, duration, surfaceWidth, viewportWidth, 0.8)).toEqual({
      time: 40,
      scrollLeft: 0,
      playheadPixels: 800,
    })
    expect(getTimelineFollowGeometry(50, duration, surfaceWidth, viewportWidth, 0.8)).toEqual({
      time: 50,
      scrollLeft: 200,
      playheadPixels: 800,
    })
  })

  it('clamps invalid and out-of-range media time', () => {
    expect(getTimelineFollowGeometry(-10, duration, surfaceWidth, viewportWidth, 0.5).time).toBe(0)
    expect(getTimelineFollowGeometry(110, duration, surfaceWidth, viewportWidth, 0.5).time).toBe(100)
    expect(getTimelineFollowGeometry(Number.NaN, duration, surfaceWidth, viewportWidth, 0.5).time).toBe(0)
  })

  it('moves across the viewport when the timeline has no overflow', () => {
    expect(getTimelineFollowGeometry(50, duration, viewportWidth, viewportWidth, 0.5)).toEqual({
      time: 50,
      scrollLeft: 0,
      playheadPixels: 500,
    })
  })
})
