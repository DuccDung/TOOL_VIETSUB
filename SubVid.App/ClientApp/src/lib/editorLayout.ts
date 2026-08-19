export type VerticalEditorLayout = {
  timelineHeight: number
  workspaceHeight: number
}

type VerticalEditorLayoutOptions = {
  editorHeight: number
  requestedTimelineHeight: number
  verticalPadding: number
  resizerSize: number
  preferredWorkspaceHeight: number
  preferredTimelineHeight: number
  minimumUsableWorkspaceHeight: number
  minimumUsableTimelineHeight: number
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(Math.max(value, minimum), maximum)
}

export function fitVerticalEditorLayout({
  editorHeight,
  requestedTimelineHeight,
  verticalPadding,
  resizerSize,
  preferredWorkspaceHeight,
  preferredTimelineHeight,
  minimumUsableWorkspaceHeight,
  minimumUsableTimelineHeight,
}: VerticalEditorLayoutOptions): VerticalEditorLayout {
  const availableHeight = Math.max(
    0,
    Math.floor(editorHeight) - verticalPadding - resizerSize,
  )
  const usableWorkspaceFloor = Math.min(
    minimumUsableWorkspaceHeight,
    Math.max(0, availableHeight - minimumUsableTimelineHeight),
  )
  const workspaceFloor = Math.min(
    Math.max(
      usableWorkspaceFloor,
      availableHeight - preferredTimelineHeight,
    ),
    preferredWorkspaceHeight,
    Math.max(0, availableHeight - minimumUsableTimelineHeight),
  )
  const timelineFloor = Math.min(
    preferredTimelineHeight,
    Math.max(
      minimumUsableTimelineHeight,
      availableHeight - preferredWorkspaceHeight,
    ),
    Math.max(0, availableHeight - workspaceFloor),
  )
  const timelineCeiling = Math.max(
    timelineFloor,
    availableHeight - workspaceFloor,
  )
  const timelineHeight = clamp(
    Math.round(requestedTimelineHeight),
    timelineFloor,
    timelineCeiling,
  )

  return {
    timelineHeight,
    workspaceHeight: Math.max(0, availableHeight - timelineHeight),
  }
}
