import { describe, expect, it } from 'vitest'
import { fitVerticalEditorLayout } from './editorLayout'

const defaults = {
  verticalPadding: 20,
  resizerSize: 10,
  preferredWorkspaceHeight: 290,
  preferredTimelineHeight: 240,
  minimumUsableWorkspaceHeight: 120,
  minimumUsableTimelineHeight: 120,
}

describe('vertical editor layout', () => {
  it('preserves the requested timeline height when the viewport has room', () => {
    expect(fitVerticalEditorLayout({
      ...defaults,
      editorHeight: 764,
      requestedTimelineHeight: 332,
    })).toEqual({
      timelineHeight: 332,
      workspaceHeight: 402,
    })
  })

  it('fits a saved timeline height without clipping the preferred workspace', () => {
    expect(fitVerticalEditorLayout({
      ...defaults,
      editorHeight: 632,
      requestedTimelineHeight: 900,
    })).toEqual({
      timelineHeight: 312,
      workspaceHeight: 290,
    })
  })

  it('keeps both editor regions reachable in a short viewport', () => {
    const result = fitVerticalEditorLayout({
      ...defaults,
      editorHeight: 443,
      requestedTimelineHeight: 332,
    })

    expect(result).toEqual({
      timelineHeight: 240,
      workspaceHeight: 173,
    })
    expect(result.timelineHeight + result.workspaceHeight + 30).toBe(443)
  })

  it('falls back to usable minimums instead of overflowing a tiny viewport', () => {
    const result = fitVerticalEditorLayout({
      ...defaults,
      editorHeight: 330,
      requestedTimelineHeight: 332,
    })

    expect(result).toEqual({
      timelineHeight: 180,
      workspaceHeight: 120,
    })
    expect(result.timelineHeight + result.workspaceHeight + 30).toBe(330)
  })
})
