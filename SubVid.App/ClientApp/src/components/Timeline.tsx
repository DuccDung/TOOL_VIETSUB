import { Fragment, memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { PointerEvent as ReactPointerEvent } from 'react'
import { createPortal } from 'react-dom'
import {
  AlignLeft,
  Bookmark,
  Captions,
  Check,
  CircleAlert,
  Copy,
  Eye,
  Film,
  Layers3,
  Link2,
  Mic2,
  Pause,
  Pencil,
  Play,
  RotateCcw,
  Scissors,
  Trash2,
  Volume2,
  VolumeX,
  X,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { formatClock } from '../lib/format'
import { hasNativeHost, postToHost, subscribeToHost } from '../lib/host'
import { getTimelineFollowGeometry } from '../lib/timelineGeometry'
import { selectVisibleTimelineItems } from '../lib/longFormVirtualization'
import {
  buildTimelineThumbnailSamples,
  prioritizeTimelineThumbnailIndices,
  timelineThumbnailCount,
  timelineThumbnailProfileVersion,
} from '../lib/timelineThumbnails'
import type { TimelineThumbnailSample, TimelineViewport } from '../lib/timelineThumbnails'
import type { SubtitleSegment, VideoInfo, VoiceBoundaryMode } from '../types'
import { CompactRange, IconButton } from './Ui'

type TimelineProps = {
  video: VideoInfo | null
  flipHorizontal: boolean
  flipVertical: boolean
  segments: SubtitleSegment[]
  playing: boolean
  playbackActive: boolean
  currentTime: number
  playbackRate: number
  sourceAudioEnabled: boolean
  sourceVolume: number
  sourceAudioAvailable: boolean
  voiceAudioEnabled: boolean
  voiceVolume: number
  voiceAudioAvailable: boolean
  voiceAudioStale: boolean
  selectedId: number | null
  focusRequest: {
    sequence: number
    timeSeconds: number
  } | null
  busy: boolean
  onTogglePlay: () => void
  onSeek: (seconds: number) => void
  onPlaybackRateChange: (rate: number) => void
  onToggleSourceAudio: () => void
  onSourceVolumeChange: (volume: number) => void
  onToggleVoiceAudio: () => void
  onVoiceVolumeChange: (volume: number) => void
  onSelectSegment: (id: number) => void
  onUpdateSegment: (cueId: string, original: string, translated: string) => void
  onSplitCue: (id: number, positionSeconds: number) => void
  onAlignCue: (id: number, positionSeconds: number) => void
  onDuplicateCue: (id: number) => void
  onDeleteCue: (id: number) => void
  onUpdateVoiceBoundary: (previousCueId: string, nextCueId: string, mode: VoiceBoundaryMode) => void
  onNotify: (title: string, description: string) => void
}

type RenderedTimelineThumbnail = TimelineThumbnailSample & {
  url: string | null
  isFallback: boolean
}

type TimelineInteractionMode = 'IDLE' | 'FOLLOWING' | 'SCRUBBING' | 'DRAGGING_ANCHOR'

const minimumTimelineSurfaceWidth = 900
const defaultPlayheadAnchorRatio = 0.5
const minimumPlayheadAnchorRatio = 0.1
const maximumPlayheadAnchorRatio = 0.9
const playheadAnchorStorageKey = 'subvid.timeline.playhead-anchor-ratio'
const playbackViewportUpdateIntervalMilliseconds = 120
const userScrubSeekIntervalMilliseconds = 50
const userScrubSettleMilliseconds = 140
export function Timeline({
  video,
  flipHorizontal,
  flipVertical,
  segments,
  playing,
  playbackActive,
  currentTime,
  playbackRate,
  sourceAudioEnabled,
  sourceVolume,
  sourceAudioAvailable,
  voiceAudioEnabled,
  voiceVolume,
  voiceAudioAvailable,
  voiceAudioStale,
  selectedId,
  focusRequest,
  busy,
  onTogglePlay,
  onSeek,
  onPlaybackRateChange,
  onToggleSourceAudio,
  onSourceVolumeChange,
  onToggleVoiceAudio,
  onVoiceVolumeChange,
  onSelectSegment,
  onUpdateSegment,
  onSplitCue,
  onAlignCue,
  onDuplicateCue,
  onDeleteCue,
  onUpdateVoiceBoundary,
  onNotify,
}: TimelineProps) {
  const [timelineZoom, setTimelineZoom] = useState(100)
  const [bookmarks, setBookmarks] = useState<number[]>([])
  const [editingSegmentId, setEditingSegmentId] = useState<number | null>(null)
  const [draftTranslation, setDraftTranslation] = useState('')
  const [editorPosition, setEditorPosition] = useState<EditorPosition | null>(null)
  const [editingBoundaryCueId, setEditingBoundaryCueId] = useState<string | null>(null)
  const [boundaryPosition, setBoundaryPosition] = useState<BoundaryPosition | null>(null)
  const [playheadAnchorRatio, setPlayheadAnchorRatio] = useState(readPlayheadAnchorRatio)
  const [draggingPlayheadAnchor, setDraggingPlayheadAnchor] = useState(false)
  const [scrubbingTimeline, setScrubbingTimeline] = useState(false)
  const [timelineViewport, setTimelineViewport] = useState<TimelineViewport>({
    scrollLeft: 0,
    clientWidth: 0,
    scrollWidth: 0,
  })
  const [thumbnailRevision, setThumbnailRevision] = useState(0)
  const timelineShellRef = useRef<HTMLDivElement>(null)
  const timelineCanvasRef = useRef<HTMLDivElement>(null)
  const timelineContentRef = useRef<HTMLDivElement>(null)
  const playheadRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<HTMLDivElement>(null)
  const editorInputRef = useRef<HTMLTextAreaElement>(null)
  const editingAnchorRef = useRef<HTMLButtonElement>(null)
  const boundaryMenuRef = useRef<HTMLDivElement>(null)
  const boundaryAnchorRef = useRef<HTMLButtonElement>(null)
  const thumbnailCacheRef = useRef<Map<string, string>>(new Map())
  const visibleThumbnailKeysRef = useRef<Set<string>>(new Set())
  const viewportUpdateTimerRef = useRef<number | null>(null)
  const programmaticScrollTargetRef = useRef<number | null>(null)
  const lastTimelineScrollLeftRef = useRef(0)
  const timelineInteractionModeRef = useRef<TimelineInteractionMode>('IDLE')
  const scrubPlayheadPixelsRef = useRef<number | null>(null)
  const scrubPointerActiveRef = useRef(false)
  const pendingScrubTimeRef = useRef<number | null>(null)
  const scrubSeekTimerRef = useRef<number | null>(null)
  const scrubSettleTimerRef = useRef<number | null>(null)
  const lastScrubSeekAtRef = useRef(0)
  const displayedTimelineTimeRef = useRef(currentTime)
  const playbackClockRef = useRef({
    mediaTime: currentTime,
    sampledAtMilliseconds: performance.now(),
  })
  const duration = Math.max(video?.durationSeconds ?? 21, 0.001)
  const tickCount = 8
  const ticks = Array.from({ length: tickCount }, (_, index) =>
    index * duration / (tickCount - 1))
  const maximumZoomScale = Math.max(4, duration / 15)
  const minimumTimelineScale = 2
  const timelineScale = minimumTimelineScale
    + (timelineZoom / 100) * (maximumZoomScale - minimumTimelineScale)
  const timelineSurfaceWidth = Math.max(
    minimumTimelineSurfaceWidth,
    timelineViewport.clientWidth * timelineScale,
  )
  const detailZoom = timelineZoom >= 70
  const selectedSegment = segments.find((segment) => segment.id === selectedId) ?? null
  const editingSegment = segments.find((segment) => segment.id === editingSegmentId) ?? null
  const editingBoundarySegment = segments.find((segment) => segment.cueId === editingBoundaryCueId) ?? null
  const selectedPhraseId = selectedSegment?.voicePhrase?.phraseId ?? null
  const cueAtPlayhead = segments.find((segment) =>
    currentTime > segment.start + 0.1 && currentTime < segment.end - 0.1) ?? null
  const sourceAudioActive = sourceAudioAvailable && sourceAudioEnabled
  const voiceAudioActive = voiceAudioAvailable && voiceAudioEnabled
  const importantTimelineIds = useMemo(() => new Set([
    ...(selectedId === null ? [] : [selectedId]),
    ...(editingSegmentId === null ? [] : [editingSegmentId]),
    ...(cueAtPlayhead === null ? [] : [cueAtPlayhead.id]),
  ]), [selectedId, editingSegmentId, cueAtPlayhead])
  const visibleTimelineSegments = useMemo(() => selectVisibleTimelineItems(
    segments,
    duration,
    timelineViewport.scrollLeft,
    timelineViewport.clientWidth,
    timelineSurfaceWidth,
    importantTimelineIds,
  ), [segments, duration, timelineViewport, timelineSurfaceWidth, importantTimelineIds])
  const timelineSegmentsByCueId = useMemo(
    () => new Map(segments.map(segment => [segment.cueId, segment])),
    [segments],
  )
  const voiceClips = useMemo(() => {
    const clips: Array<{
      key: string
      start: number
      end: number
      label: string | null
      phraseId: string | null
      hasAudio: boolean
      needsRegeneration: boolean
    }> = []
    const visitedPhrases = new Set<string>()
    for (const segment of segments) {
      const phrase = segment.voicePhrase
      if (phrase) {
        if (visitedPhrases.has(phrase.phraseId)) continue
        visitedPhrases.add(phrase.phraseId)
        const members = segments.filter((item) => item.voicePhrase?.phraseId === phrase.phraseId)
        clips.push({
          key: `phrase-${phrase.phraseId}`,
          start: Math.min(...members.map((item) => item.start)),
          end: Math.max(...members.map((item) => item.end)),
          label: `Cụm ${phrase.startCueNumber}–${phrase.endCueNumber}`,
          phraseId: phrase.phraseId,
          hasAudio: phrase.hasAudio,
          needsRegeneration: phrase.needsRegeneration,
        })
      } else if (segment.hasVoice) {
        clips.push({
          key: `cue-${segment.cueId}`,
          start: segment.start,
          end: segment.end,
          label: null,
          phraseId: null,
          hasAudio: true,
          needsRegeneration: false,
        })
      }
    }
    return clips
  }, [segments])
  const originalAudioClips = useMemo(() => {
    if (voiceClips.length > 0) return voiceClips
    if (segments.length > 0) {
      return segments.map((segment) => ({
        key: `source-${segment.cueId}`,
        start: segment.start,
        end: segment.end,
      }))
    }
    return video ? [{ key: 'source-audio', start: 0, end: duration }] : []
  }, [duration, segments, video, voiceClips])
  const thumbnailSourceKey = video
    ? `${video.sha256 ?? video.fileName}:${video.playbackUrl ?? ''}`
    : ''

  const updateTimelineViewport = useCallback(() => {
    const canvas = timelineCanvasRef.current
    if (!canvas) return
    const next = {
      scrollLeft: canvas.scrollLeft,
      clientWidth: canvas.clientWidth,
      scrollWidth: canvas.scrollWidth,
    }
    setTimelineViewport((current) => (
      Math.abs(current.scrollLeft - next.scrollLeft) < 0.5
      && current.clientWidth === next.clientWidth
      && current.scrollWidth === next.scrollWidth
        ? current
        : next
    ))
  }, [])

  const updatePlayheadVisual = useCallback((pixels: number) => {
    const playhead = playheadRef.current
    const canvas = timelineCanvasRef.current
    if (!playhead || !canvas) return
    const clampedPixels = Math.min(canvas.clientWidth, Math.max(0, pixels))
    playhead.style.left = `${clampedPixels}px`
    playhead.dataset.edge = clampedPixels <= 9
      ? 'start'
      : clampedPixels >= canvas.clientWidth - 9
        ? 'end'
        : 'none'
  }, [])

  const scheduleTimelineViewportUpdate = useCallback(() => {
    if (!playing) {
      updateTimelineViewport()
      return
    }

    if (viewportUpdateTimerRef.current !== null) return
    viewportUpdateTimerRef.current = window.setTimeout(() => {
      viewportUpdateTimerRef.current = null
      updateTimelineViewport()
    }, playbackViewportUpdateIntervalMilliseconds)
  }, [playing, updateTimelineViewport])

  const emitThrottledScrubSeek = useCallback((seconds: number) => {
    pendingScrubTimeRef.current = seconds
    const elapsed = performance.now() - lastScrubSeekAtRef.current
    if (elapsed >= userScrubSeekIntervalMilliseconds) {
      lastScrubSeekAtRef.current = performance.now()
      onSeek(seconds)
      return
    }

    if (scrubSeekTimerRef.current !== null) return
    scrubSeekTimerRef.current = window.setTimeout(() => {
      scrubSeekTimerRef.current = null
      const pendingTime = pendingScrubTimeRef.current
      if (pendingTime === null) return
      lastScrubSeekAtRef.current = performance.now()
      onSeek(pendingTime)
    }, userScrubSeekIntervalMilliseconds - elapsed)
  }, [onSeek])

  const finishTimelineScrub = useCallback(() => {
    if (timelineInteractionModeRef.current !== 'SCRUBBING') return
    if (scrubSeekTimerRef.current !== null) {
      window.clearTimeout(scrubSeekTimerRef.current)
      scrubSeekTimerRef.current = null
    }
    if (scrubSettleTimerRef.current !== null) {
      window.clearTimeout(scrubSettleTimerRef.current)
      scrubSettleTimerRef.current = null
    }

    const finalTime = pendingScrubTimeRef.current
    timelineInteractionModeRef.current = 'IDLE'
    scrubPlayheadPixelsRef.current = null
    scrubPointerActiveRef.current = false
    pendingScrubTimeRef.current = null
    setScrubbingTimeline(false)
    if (finalTime !== null) onSeek(finalTime)
  }, [onSeek])

  const scheduleTimelineScrubFinish = useCallback(() => {
    if (scrubSettleTimerRef.current !== null) {
      window.clearTimeout(scrubSettleTimerRef.current)
    }
    scrubSettleTimerRef.current = window.setTimeout(() => {
      scrubSettleTimerRef.current = null
      if (!scrubPointerActiveRef.current) finishTimelineScrub()
    }, userScrubSettleMilliseconds)
  }, [finishTimelineScrub])

  const beginTimelineScrub = useCallback((pointerActive: boolean) => {
    const canvas = timelineCanvasRef.current
    if (!canvas
      || busy
      || draggingPlayheadAnchor
      || editingSegmentId !== null
      || editingBoundaryCueId !== null) {
      return false
    }

    if (timelineInteractionModeRef.current !== 'SCRUBBING') {
      const timePixels = Math.min(duration, Math.max(0, displayedTimelineTimeRef.current))
        / duration
        * timelineSurfaceWidth
      scrubPlayheadPixelsRef.current = Math.min(
        canvas.clientWidth,
        Math.max(0, timePixels - lastTimelineScrollLeftRef.current),
      )
      timelineInteractionModeRef.current = 'SCRUBBING'
      programmaticScrollTargetRef.current = null
      pendingScrubTimeRef.current = null
      setScrubbingTimeline(true)
      if (playing) onTogglePlay()
    }
    scrubPointerActiveRef.current = scrubPointerActiveRef.current || pointerActive
    return true
  }, [
    busy,
    draggingPlayheadAnchor,
    duration,
    editingBoundaryCueId,
    editingSegmentId,
    onTogglePlay,
    playing,
    timelineSurfaceWidth,
  ])

  const positionTimelineAtTime = useCallback((seconds: number) => {
    const canvas = timelineCanvasRef.current
    if (!canvas) return
    if (timelineInteractionModeRef.current === 'SCRUBBING'
      || timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') return

    const geometry = getTimelineFollowGeometry(
      seconds,
      duration,
      timelineSurfaceWidth,
      canvas.clientWidth,
      playheadAnchorRatio,
    )
    displayedTimelineTimeRef.current = geometry.time
    updatePlayheadVisual(geometry.playheadPixels)
    if (Math.abs(canvas.scrollLeft - geometry.scrollLeft) > 0.25) {
      timelineInteractionModeRef.current = 'FOLLOWING'
      programmaticScrollTargetRef.current = geometry.scrollLeft
      lastTimelineScrollLeftRef.current = geometry.scrollLeft
      canvas.scrollLeft = geometry.scrollLeft
    } else {
      lastTimelineScrollLeftRef.current = canvas.scrollLeft
      programmaticScrollTargetRef.current = null
      if (timelineInteractionModeRef.current === 'FOLLOWING' && !playbackActive) {
        timelineInteractionModeRef.current = 'IDLE'
      }
    }
  }, [duration, playbackActive, playheadAnchorRatio, timelineSurfaceWidth, updatePlayheadVisual])

  const handleTimelineScroll = useCallback(() => {
    const canvas = timelineCanvasRef.current
    if (!canvas) return
    const previousScrollLeft = lastTimelineScrollLeftRef.current
    lastTimelineScrollLeftRef.current = canvas.scrollLeft
    scheduleTimelineViewportUpdate()

    const programmaticTarget = programmaticScrollTargetRef.current
    if (timelineInteractionModeRef.current === 'FOLLOWING') {
      if (programmaticTarget !== null
        && Math.abs(canvas.scrollLeft - programmaticTarget) <= 1) {
        programmaticScrollTargetRef.current = null
        if (!playbackActive) timelineInteractionModeRef.current = 'IDLE'
      }
      // A scroll event can arrive after the latest animation frame has already
      // cleared its exact target. While media is running, FOLLOWING still means
      // this scroll belongs to the playhead, not to a user scrub gesture.
      if (playbackActive) return
      if (programmaticTarget !== null) return
    }

    if (timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') {
      return
    }
    if (timelineSurfaceWidth <= 0 || !beginTimelineScrub(false)) return

    const timePixels = Math.min(duration, Math.max(0, displayedTimelineTimeRef.current))
      / duration
      * timelineSurfaceWidth
    const playheadPixels = scrubPlayheadPixelsRef.current ?? Math.min(
      canvas.clientWidth,
      Math.max(0, timePixels - previousScrollLeft),
    )
    scrubPlayheadPixelsRef.current = playheadPixels
    updatePlayheadVisual(playheadPixels)
    const selectedTime = Math.min(
      duration,
      Math.max(0, (canvas.scrollLeft + playheadPixels) / timelineSurfaceWidth * duration),
    )
    displayedTimelineTimeRef.current = selectedTime
    emitThrottledScrubSeek(selectedTime)
    scheduleTimelineScrubFinish()
  }, [
    beginTimelineScrub,
    duration,
    emitThrottledScrubSeek,
    playbackActive,
    scheduleTimelineViewportUpdate,
    scheduleTimelineScrubFinish,
    timelineSurfaceWidth,
    updatePlayheadVisual,
  ])

  const updatePlayheadAnchorFromPointer = useCallback((clientX: number) => {
    const shell = timelineShellRef.current
    if (!shell) return
    const bounds = shell.getBoundingClientRect()
    if (bounds.width <= 0) return
    const nextRatio = Math.min(
      maximumPlayheadAnchorRatio,
      Math.max(minimumPlayheadAnchorRatio, (clientX - bounds.left) / bounds.width),
    )
    updatePlayheadVisual(nextRatio * bounds.width)
    setPlayheadAnchorRatio(nextRatio)
  }, [updatePlayheadVisual])

  const beginPlayheadAnchorDrag = useCallback((event: ReactPointerEvent<HTMLButtonElement>) => {
    if (busy) return
    event.preventDefault()
    event.stopPropagation()
    event.currentTarget.setPointerCapture(event.pointerId)
    timelineInteractionModeRef.current = 'DRAGGING_ANCHOR'
    programmaticScrollTargetRef.current = null
    setDraggingPlayheadAnchor(true)
    updatePlayheadAnchorFromPointer(event.clientX)
  }, [busy, updatePlayheadAnchorFromPointer])

  const movePlayheadAnchor = useCallback((event: ReactPointerEvent<HTMLButtonElement>) => {
    if (!draggingPlayheadAnchor || !event.currentTarget.hasPointerCapture(event.pointerId)) return
    event.preventDefault()
    updatePlayheadAnchorFromPointer(event.clientX)
  }, [draggingPlayheadAnchor, updatePlayheadAnchorFromPointer])

  const finishPlayheadAnchorDrag = useCallback(() => {
    if (timelineInteractionModeRef.current !== 'DRAGGING_ANCHOR') return
    timelineInteractionModeRef.current = 'IDLE'
    programmaticScrollTargetRef.current = null
    setDraggingPlayheadAnchor(false)
    window.requestAnimationFrame(() => positionTimelineAtTime(currentTime))
  }, [currentTime, positionTimelineAtTime])

  const endPlayheadAnchorDrag = useCallback((event: ReactPointerEvent<HTMLButtonElement>) => {
    if (timelineInteractionModeRef.current !== 'DRAGGING_ANCHOR') return
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId)
    }
    finishPlayheadAnchorDrag()
  }, [finishPlayheadAnchorDrag])

  const thumbnailSamples = useMemo(
    () => buildTimelineThumbnailSamples(
      duration,
      timelineViewport,
      thumbnailSourceKey,
      timelineSurfaceWidth,
    ),
    [
      duration,
      thumbnailSourceKey,
      timelineSurfaceWidth,
      timelineViewport,
    ],
  )
  visibleThumbnailKeysRef.current = new Set(
    thumbnailSamples.map((sample) => sample.cacheKey),
  )

  const timelineThumbnails = useMemo(() => thumbnailSamples.map((sample) => {
    const exactUrl = thumbnailCacheRef.current.get(sample.cacheKey) ?? null
    const fallbackUrl = exactUrl ?? findNearestThumbnailUrl(
      sample,
      thumbnailSamples,
      thumbnailCacheRef.current,
    )
    return {
      ...sample,
      url: fallbackUrl,
      isFallback: exactUrl === null && fallbackUrl !== null,
    }
  }), [thumbnailRevision, thumbnailSamples])

  const closeEditor = useCallback(() => {
    setEditingSegmentId(null)
    setDraftTranslation('')
    setEditorPosition(null)
    editingAnchorRef.current = null
  }, [])

  const saveEditor = useCallback(() => {
    if (!editingSegment || busy) return
    const normalized = draftTranslation.trim()
    if (normalized !== editingSegment.translated.trim()) {
      onUpdateSegment(editingSegment.cueId, editingSegment.original, normalized)
    }
    closeEditor()
  }, [busy, closeEditor, draftTranslation, editingSegment, onUpdateSegment])

  const openEditor = useCallback((segment: SubtitleSegment, anchor: HTMLButtonElement) => {
    if (busy) return
    setEditingBoundaryCueId(null)
    setBoundaryPosition(null)
    boundaryAnchorRef.current = null
    if (playing) onTogglePlay()
    onSelectSegment(segment.id)
    onSeek(segment.start)
    editingAnchorRef.current = anchor
    setEditingSegmentId(segment.id)
    setDraftTranslation(segment.translated)
    setEditorPosition(getEditorPosition(anchor))
  }, [busy, onSeek, onSelectSegment, onTogglePlay, playing])

  const closeBoundaryMenu = useCallback(() => {
    setEditingBoundaryCueId(null)
    setBoundaryPosition(null)
    boundaryAnchorRef.current = null
  }, [])

  const openBoundaryMenu = useCallback((segment: SubtitleSegment, anchor: HTMLButtonElement) => {
    if (busy || !segment.voiceBoundaryAfter) return
    closeEditor()
    if (playing) onTogglePlay()
    boundaryAnchorRef.current = anchor
    setEditingBoundaryCueId(segment.cueId)
    setBoundaryPosition(getBoundaryPosition(anchor))
  }, [busy, closeEditor, onTogglePlay, playing])

  const applyBoundaryMode = useCallback((mode: VoiceBoundaryMode) => {
    const boundary = editingBoundarySegment?.voiceBoundaryAfter
    if (!editingBoundarySegment || !boundary || busy) return
    onUpdateVoiceBoundary(editingBoundarySegment.cueId, boundary.nextCueId, mode)
    closeBoundaryMenu()
  }, [busy, closeBoundaryMenu, editingBoundarySegment, onUpdateVoiceBoundary])

  useEffect(() => {
    playbackClockRef.current = {
      mediaTime: Math.min(duration, Math.max(0, currentTime)),
      sampledAtMilliseconds: performance.now(),
    }
    if (timelineInteractionModeRef.current !== 'SCRUBBING') {
      displayedTimelineTimeRef.current = Math.min(duration, Math.max(0, currentTime))
    }
  }, [currentTime, duration, playbackActive, playbackRate])

  useEffect(() => {
    if (!playbackActive) return
    let animationFrame = 0
    const followPlayback = (now: number) => {
      if (timelineInteractionModeRef.current === 'SCRUBBING'
        || timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') {
        animationFrame = window.requestAnimationFrame(followPlayback)
        return
      }
      const clock = playbackClockRef.current
      const elapsedSeconds = Math.max(0, now - clock.sampledAtMilliseconds) / 1_000
      positionTimelineAtTime(Math.min(
        duration,
        clock.mediaTime + elapsedSeconds * playbackRate,
      ))
      animationFrame = window.requestAnimationFrame(followPlayback)
    }
    animationFrame = window.requestAnimationFrame(followPlayback)
    return () => window.cancelAnimationFrame(animationFrame)
  }, [duration, playbackActive, playbackRate, positionTimelineAtTime])

  useEffect(() => {
    if (playbackActive) return
    if (timelineInteractionModeRef.current === 'SCRUBBING'
      || timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') return
    const frame = window.requestAnimationFrame(() => positionTimelineAtTime(currentTime))
    return () => window.cancelAnimationFrame(frame)
  }, [currentTime, playbackActive, positionTimelineAtTime, timelineViewport.clientWidth])

  useEffect(() => {
    try {
      window.localStorage.setItem(playheadAnchorStorageKey, playheadAnchorRatio.toString())
    } catch {
      // The timeline preference remains available for this session if storage is blocked.
    }
  }, [playheadAnchorRatio])

  useEffect(() => {
    const releasePointerInteraction = () => {
      if (timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') {
        finishPlayheadAnchorDrag()
        return
      }
      if (timelineInteractionModeRef.current !== 'SCRUBBING'
        || !scrubPointerActiveRef.current) return
      scrubPointerActiveRef.current = false
      scheduleTimelineScrubFinish()
    }
    const releaseOnBlur = () => {
      if (timelineInteractionModeRef.current === 'DRAGGING_ANCHOR') {
        finishPlayheadAnchorDrag()
      } else if (timelineInteractionModeRef.current === 'SCRUBBING') {
        scrubPointerActiveRef.current = false
        finishTimelineScrub()
      }
    }
    window.addEventListener('pointerup', releasePointerInteraction)
    window.addEventListener('pointercancel', releasePointerInteraction)
    window.addEventListener('blur', releaseOnBlur)
    return () => {
      window.removeEventListener('pointerup', releasePointerInteraction)
      window.removeEventListener('pointercancel', releasePointerInteraction)
      window.removeEventListener('blur', releaseOnBlur)
    }
  }, [finishPlayheadAnchorDrag, finishTimelineScrub, scheduleTimelineScrubFinish])

  useEffect(() => {
    scrubPlayheadPixelsRef.current = null
    scrubPointerActiveRef.current = false
    pendingScrubTimeRef.current = null
    setDraggingPlayheadAnchor(false)
    setScrubbingTimeline(false)
    setPlayheadAnchorRatio(defaultPlayheadAnchorRatio)
    const canvas = timelineCanvasRef.current
    if (canvas && Math.abs(canvas.scrollLeft) > 0.25) {
      timelineInteractionModeRef.current = 'FOLLOWING'
      programmaticScrollTargetRef.current = 0
      lastTimelineScrollLeftRef.current = 0
      canvas.scrollLeft = 0
    } else {
      timelineInteractionModeRef.current = 'IDLE'
      programmaticScrollTargetRef.current = null
    }
    displayedTimelineTimeRef.current = 0
    updatePlayheadVisual(0)
  }, [thumbnailSourceKey, updatePlayheadVisual])

  useEffect(() => () => {
    if (viewportUpdateTimerRef.current !== null) {
      window.clearTimeout(viewportUpdateTimerRef.current)
      viewportUpdateTimerRef.current = null
    }
    if (scrubSeekTimerRef.current !== null) {
      window.clearTimeout(scrubSeekTimerRef.current)
      scrubSeekTimerRef.current = null
    }
    if (scrubSettleTimerRef.current !== null) {
      window.clearTimeout(scrubSettleTimerRef.current)
      scrubSettleTimerRef.current = null
    }
  }, [])

  useEffect(() => {
    const canvas = timelineCanvasRef.current
    if (!canvas || !focusRequest) return

    const frame = window.requestAnimationFrame(() => {
      positionTimelineAtTime(focusRequest.timeSeconds)
    })

    return () => window.cancelAnimationFrame(frame)
  }, [focusRequest, positionTimelineAtTime])

  useEffect(() => {
    const canvas = timelineCanvasRef.current
    const content = timelineContentRef.current
    if (!canvas) return

    const update = () => window.requestAnimationFrame(updateTimelineViewport)
    updateTimelineViewport()
    const observer = new ResizeObserver(update)
    observer.observe(canvas)
    if (content) observer.observe(content)
    return () => observer.disconnect()
  }, [timelineScale, updateTimelineViewport, video?.fileName])

  useEffect(() => {
    thumbnailCacheRef.current.clear()
    setThumbnailRevision((value) => value + 1)

    const sourceSha256 = video?.sha256?.toLowerCase()
    if (!sourceSha256 || !hasNativeHost()) return
    return subscribeToHost((message) => {
      if (message.type !== 'timeline:thumbnail:ready'
        || String(message.sourceSha256 ?? '').toLowerCase() !== sourceSha256) return
      const index = Number(message.index)
      const url = String(message.url ?? '')
      if (!Number.isInteger(index)
        || index < 0
        || index >= timelineThumbnailCount
        || !url.startsWith('https://media.subvid.local/thumbnail/')) return

      const cacheKey = `${thumbnailSourceKey}:v${timelineThumbnailProfileVersion}:${index}`
      if (thumbnailCacheRef.current.get(cacheKey) === url) return
      thumbnailCacheRef.current.set(cacheKey, url)
      if (visibleThumbnailKeysRef.current.has(cacheKey)) {
        setThumbnailRevision((value) => value + 1)
      }
    })
  }, [thumbnailSourceKey, video?.sha256])

  useEffect(() => {
    const missingSamples = thumbnailSamples.filter(
      (sample) => !thumbnailCacheRef.current.has(sample.cacheKey),
    )
    const sourceSha256 = video?.sha256?.toLowerCase()
    if (!sourceSha256 || !hasNativeHost() || missingSamples.length === 0) return

    const viewportCenterTime = (
      (timelineViewport.scrollLeft + timelineViewport.clientWidth / 2)
      / Math.max(1, timelineSurfaceWidth)
    ) * duration
    postToHost('timeline:thumbnails:request', {
      sourceSha256,
      indices: prioritizeTimelineThumbnailIndices(missingSamples, viewportCenterTime),
    })
  }, [
    duration,
    thumbnailSamples,
    timelineSurfaceWidth,
    timelineViewport.clientWidth,
    timelineViewport.scrollLeft,
    video?.sha256,
  ])

  useEffect(() => {
    if (editingSegmentId === null) return
    const frame = window.requestAnimationFrame(() => {
      editorInputRef.current?.focus()
      editorInputRef.current?.select()
    })
    return () => window.cancelAnimationFrame(frame)
  }, [editingSegmentId])

  useEffect(() => {
    if (editingSegmentId === null) return
    const updatePosition = () => {
      if (editingAnchorRef.current) {
        setEditorPosition(getEditorPosition(editingAnchorRef.current))
      }
    }
    window.addEventListener('resize', updatePosition)
    window.addEventListener('scroll', updatePosition, true)
    return () => {
      window.removeEventListener('resize', updatePosition)
      window.removeEventListener('scroll', updatePosition, true)
    }
  }, [editingSegmentId])

  useEffect(() => {
    if (editingSegmentId === null) return
    const saveWhenClickingOutside = (event: PointerEvent) => {
      const target = event.target
      if (!(target instanceof Node)
        || editorRef.current?.contains(target)
        || editingAnchorRef.current?.contains(target)) {
        return
      }
      saveEditor()
    }
    document.addEventListener('pointerdown', saveWhenClickingOutside, true)
    return () => document.removeEventListener('pointerdown', saveWhenClickingOutside, true)
  }, [editingSegmentId, saveEditor])

  useEffect(() => {
    if (editingSegmentId !== null && !editingSegment) closeEditor()
  }, [closeEditor, editingSegment, editingSegmentId])

  useEffect(() => {
    if (editingBoundaryCueId === null) return
    const updatePosition = () => {
      if (boundaryAnchorRef.current) {
        setBoundaryPosition(getBoundaryPosition(boundaryAnchorRef.current))
      }
    }
    const closeWhenClickingOutside = (event: PointerEvent) => {
      const target = event.target
      if (!(target instanceof Node)
        || boundaryMenuRef.current?.contains(target)
        || boundaryAnchorRef.current?.contains(target)) {
        return
      }
      closeBoundaryMenu()
    }
    window.addEventListener('resize', updatePosition)
    window.addEventListener('scroll', updatePosition, true)
    document.addEventListener('pointerdown', closeWhenClickingOutside, true)
    return () => {
      window.removeEventListener('resize', updatePosition)
      window.removeEventListener('scroll', updatePosition, true)
      document.removeEventListener('pointerdown', closeWhenClickingOutside, true)
    }
  }, [closeBoundaryMenu, editingBoundaryCueId])

  useEffect(() => {
    if (editingBoundaryCueId !== null && !editingBoundarySegment) closeBoundaryMenu()
  }, [closeBoundaryMenu, editingBoundaryCueId, editingBoundarySegment])

  const readingEstimate = editingSegment
    ? getReadingEstimate(draftTranslation, editingSegment.end - editingSegment.start)
    : null

  return (
    <>
      <section
      className={`timeline-section ${detailZoom ? 'timeline-section--detail' : ''}`}
      aria-label="Dòng thời gian"
    >
      <div className="transport-bar">
        <time className="main-timecode">{formatClock(currentTime, true)}</time>

        <div className="transport-tools" role="toolbar" aria-label="Công cụ timeline">
          <IconButton
            label="Tách cue tại playhead"
            size="small"
            disabled={busy || !cueAtPlayhead}
            onClick={() => cueAtPlayhead && onSplitCue(cueAtPlayhead.id, currentTime)}
          ><Scissors size={16} /></IconButton>
          <IconButton
            label="Căn đầu cue đã chọn vào playhead"
            size="small"
            disabled={busy || !selectedSegment}
            onClick={() => selectedSegment && onAlignCue(selectedSegment.id, currentTime)}
          ><AlignLeft size={16} /></IconButton>
          <IconButton
            label="Nhân bản cue đã chọn"
            size="small"
            disabled={busy || !selectedSegment}
            onClick={() => selectedSegment && onDuplicateCue(selectedSegment.id)}
          ><Copy size={16} /></IconButton>
          <IconButton
            label="Xóa cue đã chọn"
            size="small"
            disabled={busy || !selectedSegment}
            onClick={() => {
              if (selectedSegment && window.confirm('Xóa phân đoạn phụ đề đã chọn?')) {
                onDeleteCue(selectedSegment.id)
              }
            }}
          ><Trash2 size={16} /></IconButton>
          <IconButton
            label="Đánh dấu playhead"
            size="small"
            active={bookmarks.some((value) => Math.abs(value - currentTime) < 0.05)}
            aria-pressed={bookmarks.some((value) => Math.abs(value - currentTime) < 0.05)}
            disabled={!video}
            onClick={() => setBookmarks((items) => items.some((value) => Math.abs(value - currentTime) < 0.05)
              ? items.filter((value) => Math.abs(value - currentTime) >= 0.05)
              : [...items, currentTime])}
          ><Bookmark size={16} /></IconButton>
        </div>

        <button
          type="button"
          className={`transport-play ${playing ? 'is-playing' : ''}`}
          aria-label={playing ? 'Tạm dừng' : 'Phát'}
          disabled={!video}
          onClick={onTogglePlay}
        >
          {playing ? <Pause size={18} fill="currentColor" /> : <Play size={18} fill="currentColor" />}
        </button>

        <button
          type="button"
          className="batch-button"
          onClick={() => onNotify('Batch Mode', 'Xử lý hàng loạt chưa thuộc phạm vi V1.')}
        >
          <Layers3 size={16} />
          <span>Batch Mode</span>
          <small>TẮT</small>
        </button>

        <div className="transport-spacer" />

        <CompactRange
          label="Tốc độ xem trước (không ảnh hưởng file xuất)"
          value={playbackRate}
          min={0.5}
          max={2}
          step={0.1}
          suffix="x"
          icon={<Play size={14} />}
          onChange={onPlaybackRateChange}
        />
        <CompactRange
          label="Âm lượng gốc"
          value={sourceVolume}
          icon={sourceAudioActive ? <Volume2 size={16} /> : <VolumeX size={16} />}
          disabled={busy || !sourceAudioActive}
          onChange={onSourceVolumeChange}
        />
        <CompactRange
          label="Âm lượng giọng Việt"
          value={voiceVolume}
          icon={<Mic2 size={16} />}
          disabled={busy || !voiceAudioActive}
          onChange={onVoiceVolumeChange}
        />
        <CompactRange
          label="Thu phóng timeline"
          value={timelineZoom}
          icon={<ZoomOut size={16} />}
          onChange={setTimelineZoom}
        />
        <ZoomIn size={15} className="zoom-end-icon" aria-hidden="true" />
      </div>

      <div className="timeline-body">
        <div className="track-sidebar">
          <div className="ruler-corner" />
          <div className="track-label">
            <span><Eye size={14} /></span>
            <Film size={15} />
            <strong>Video</strong>
          </div>
          <div className="track-label">
            <span><Eye size={14} /></span>
            <Captions size={15} />
            <strong>Phụ đề</strong>
          </div>
          <div className={`track-label ${sourceAudioActive ? '' : 'is-muted'} ${sourceAudioAvailable ? '' : 'is-unavailable'}`}>
            <button
              type="button"
              className="track-audio-toggle"
              aria-label={sourceAudioActive ? 'Tắt Âm gốc' : 'Bật Âm gốc'}
              aria-pressed={sourceAudioActive}
              title={sourceAudioAvailable
                ? sourceAudioActive ? 'Tắt Âm gốc' : 'Bật Âm gốc'
                : 'Video không có âm thanh gốc'}
              disabled={busy || !sourceAudioAvailable}
              onClick={onToggleSourceAudio}
            >
              {sourceAudioActive ? <Volume2 size={14} /> : <VolumeX size={14} />}
            </button>
            <Volume2 size={15} />
            <strong>Âm gốc</strong>
          </div>
          <div className={`track-label ${voiceAudioActive ? '' : 'is-muted'} ${voiceAudioAvailable ? '' : 'is-unavailable'} ${voiceAudioStale ? 'is-stale' : ''}`}>
            <button
              type="button"
              className="track-audio-toggle"
              aria-label={voiceAudioActive ? 'Tắt Giọng Việt' : 'Bật Giọng Việt'}
              aria-pressed={voiceAudioActive}
              title={voiceAudioAvailable
                ? voiceAudioStale
                  ? 'Đây là bản nghe trước khi sửa text. Hãy tạo giọng lại để cập nhật.'
                  : voiceAudioActive ? 'Tắt Giọng Việt' : 'Bật Giọng Việt'
                : 'Hãy tạo giọng Việt trước'}
              disabled={busy || !voiceAudioAvailable}
              onClick={onToggleVoiceAudio}
            >
              {voiceAudioActive ? <Volume2 size={14} /> : <VolumeX size={14} />}
            </button>
            <Mic2 size={15} />
            <strong>{voiceAudioStale ? 'Giọng Việt · bản cũ' : 'Giọng Việt'}</strong>
          </div>
        </div>

        <div ref={timelineShellRef} className="timeline-canvas-shell">
          <div
            ref={timelineCanvasRef}
            className="timeline-canvas"
            onScroll={handleTimelineScroll}
            onPointerDownCapture={(event) => {
              if (event.target === event.currentTarget) beginTimelineScrub(true)
            }}
            onPointerUpCapture={() => {
              if (timelineInteractionModeRef.current !== 'SCRUBBING') return
              scrubPointerActiveRef.current = false
              scheduleTimelineScrubFinish()
            }}
            onPointerCancelCapture={() => {
              if (timelineInteractionModeRef.current !== 'SCRUBBING') return
              scrubPointerActiveRef.current = false
              scheduleTimelineScrubFinish()
            }}
            onWheelCapture={(event) => {
              if (Math.abs(event.deltaX) > 0.01) beginTimelineScrub(false)
            }}
          >
            <div
              ref={timelineContentRef}
              className="timeline-scroll-content"
              style={{
                width: `${timelineSurfaceWidth}px`,
              }}
            >
            <div
              className="timeline-time-surface"
              style={{ width: `${timelineSurfaceWidth}px` }}
            >
            <div
              className="timeline-ruler"
              role="slider"
              tabIndex={0}
              aria-label="Vị trí phát trên dòng thời gian"
              aria-valuemin={0}
              aria-valuemax={duration}
              aria-valuenow={Math.round(currentTime * 10) / 10}
              onPointerDown={(event) => {
                if (editingSegmentId !== null) {
                  event.preventDefault()
                  return
                }
                const rect = event.currentTarget.getBoundingClientRect()
                onSeek(((event.clientX - rect.left) / rect.width) * duration)
              }}
              onKeyDown={(event) => {
                if (editingSegmentId !== null) return
                if (event.key === 'ArrowLeft') onSeek(Math.max(0, currentTime - 0.25))
                if (event.key === 'ArrowRight') onSeek(Math.min(duration, currentTime + 0.25))
                if (event.key === 'Home') onSeek(0)
                if (event.key === 'End') onSeek(duration)
              }}
            >
              {ticks.map((tick) => (
                <span key={tick} style={{ left: `${(tick / duration) * 100}%` }}>
                  {formatClock(tick)}
                </span>
              ))}
              {bookmarks.map((bookmark) => (
                <i
                  key={bookmark}
                  className="timeline-bookmark"
                  style={{ left: `${(bookmark / duration) * 100}%` }}
                  aria-hidden="true"
                />
              ))}
            </div>

            <div className="timeline-tracks">
            <div className="timeline-track video-track">
              {video ? (
                <div className="video-clip">
                  <TimelineFilmstrip
                    thumbnails={timelineThumbnails}
                    duration={duration}
                    flipHorizontal={flipHorizontal}
                    flipVertical={flipVertical}
                  />
                  <span className="video-clip__label">
                    <Film size={13} />
                    <span>{video.fileName}</span>
                  </span>
                </div>
              ) : null}
            </div>
            <div className="timeline-track subtitle-track">
              {visibleTimelineSegments.map((segment) => {
                const displayText = segment.translated.trim()
                  || segment.original.trim()
                  || `Phân đoạn ${segment.id}`
                const isPhrasePeer = selectedPhraseId !== null
                  && segment.voicePhrase?.phraseId === selectedPhraseId
                  && selectedId !== segment.id
                const boundary = segment.voiceBoundaryAfter
                const nextSegment = boundary
                  ? timelineSegmentsByCueId.get(boundary.nextCueId)
                  : null

                return (
                  <Fragment key={segment.id}>
                    <button
                      type="button"
                      className={`subtitle-clip ${currentTime >= segment.start && currentTime < segment.end ? 'is-current' : ''} ${selectedId === segment.id ? 'is-selected' : ''} ${isPhrasePeer ? 'is-phrase-peer' : ''} ${editingSegmentId === segment.id ? 'is-editing' : ''}`}
                      style={{
                        left: `${(segment.start / duration) * 100}%`,
                        width: `${((segment.end - segment.start) / duration) * 100}%`,
                      }}
                      title={`${formatClock(segment.start)} — ${formatClock(segment.end)}\n${displayText}\nNhấp đúp để sửa bản dịch`}
                      aria-label={`Phân đoạn ${segment.id}, ${formatClock(segment.start)} đến ${formatClock(segment.end)}: ${displayText}`}
                      onClick={() => {
                        if (editingSegmentId !== null) return
                        onSelectSegment(segment.id)
                        onSeek(segment.start)
                      }}
                      onDoubleClick={(event) => openEditor(segment, event.currentTarget)}
                      onKeyDown={(event) => {
                        if (event.key === 'Enter' || event.key === 'F2') {
                          event.preventDefault()
                          openEditor(segment, event.currentTarget)
                        }
                      }}
                    >
                      <span className="subtitle-clip__text">{displayText}</span>
                      <Pencil className="subtitle-clip__edit-icon" size={10} aria-hidden="true" />
                    </button>
                    {boundary && nextSegment ? (
                      <button
                        type="button"
                        className={`voice-boundary-marker voice-boundary-marker--${boundary.mode.toLowerCase()} is-effective-${boundary.effectiveMode.toLowerCase()} ${boundary.canJoin ? '' : 'is-join-blocked'} ${editingBoundaryCueId === segment.cueId ? 'is-open' : ''}`}
                        style={{ left: `${(nextSegment.start / duration) * 100}%` }}
                        disabled={busy}
                        title={`Cue ${segment.id} → ${nextSegment.id}: ${formatBoundaryLabel(boundary.mode, boundary.effectiveMode)}${boundary.constraintMessage ? `\n${boundary.constraintMessage}` : ''}`}
                        aria-label={`Chỉnh nối hoặc ngắt giữa cue ${segment.id} và cue ${nextSegment.id}`}
                        onClick={(event) => openBoundaryMenu(segment, event.currentTarget)}
                      >
                        {boundary.mode === 'JOIN'
                          ? <Link2 size={10} />
                          : boundary.mode === 'BREAK'
                            ? <Pause size={10} />
                            : <RotateCcw size={9} />}
                      </button>
                    ) : null}
                  </Fragment>
                )
              })}
            </div>
            <div className={`timeline-track audio-track ${sourceAudioActive ? '' : 'is-muted'}`}>
              {originalAudioClips.map((clip) => (
                <div
                  key={clip.key}
                  className="voice-clip original-audio-clip"
                  style={{
                    left: `${(clip.start / duration) * 100}%`,
                    width: `${((clip.end - clip.start) / duration) * 100}%`,
                  }}
                >
                  <Waveform kind="original" />
                </div>
              ))}
            </div>
            <div className={`timeline-track voice-track ${voiceAudioActive ? '' : 'is-muted'}`}>
              {voiceClips.map((clip) => (
                <div
                  key={clip.key}
                  className={`voice-clip ${clip.phraseId ? 'is-phrase' : ''} ${clip.phraseId === selectedPhraseId ? 'is-selected-phrase' : ''} ${clip.needsRegeneration ? 'needs-regeneration' : ''}`}
                  style={{
                    left: `${(clip.start / duration) * 100}%`,
                    width: `${((clip.end - clip.start) / duration) * 100}%`,
                  }}
                  title={clip.needsRegeneration
                    ? `${clip.label ?? 'Đoạn giọng'} cần tạo lại`
                    : clip.label ?? undefined}
                >
                  {clip.hasAudio ? <Waveform kind="voice" /> : null}
                  {clip.label ? <span className="voice-clip__label">{clip.label}</span> : null}
                </div>
              ))}
            </div>

            </div>
            </div>
          </div>
          </div>
          <div
            ref={playheadRef}
            className={`playhead ${draggingPlayheadAnchor ? 'is-dragging' : ''} ${scrubbingTimeline ? 'is-scrubbing' : ''}`}
          >
            <button
              type="button"
              className="playhead__anchor"
              aria-label="Kéo để chọn vị trí đứng của playhead"
              title="Kéo để chọn vị trí đứng của playhead"
              disabled={busy}
              onPointerDown={beginPlayheadAnchorDrag}
              onPointerMove={movePlayheadAnchor}
              onPointerUp={endPlayheadAnchorDrag}
              onPointerCancel={endPlayheadAnchorDrag}
              onLostPointerCapture={finishPlayheadAnchorDrag}
              onKeyDown={(event) => {
                if (event.key === 'Escape') {
                  event.preventDefault()
                  finishPlayheadAnchorDrag()
                  return
                }
                if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
                event.preventDefault()
                setPlayheadAnchorRatio((value) => Math.min(
                  maximumPlayheadAnchorRatio,
                  Math.max(
                    minimumPlayheadAnchorRatio,
                    value + (event.key === 'ArrowLeft' ? -0.05 : 0.05),
                  ),
                ))
              }}
            >
              <span aria-hidden="true" />
            </button>
          </div>
        </div>
      </div>
      </section>
      {editingBoundarySegment?.voiceBoundaryAfter && boundaryPosition ? createPortal(
        <div
          ref={boundaryMenuRef}
          className="voice-boundary-menu"
          role="dialog"
          aria-modal="false"
          aria-label="Chỉnh nối hoặc ngắt cụm thoại"
          style={boundaryPosition}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              event.preventDefault()
              closeBoundaryMenu()
            }
          }}
        >
          <div className="voice-boundary-menu__header">
            <span>Ranh giới cụm thoại</span>
            <strong>
              Cue {editingBoundarySegment.id} → Cue{' '}
              {segments.find((item) => (
                item.cueId === editingBoundarySegment.voiceBoundaryAfter?.nextCueId
              ))?.id ?? '?'}
            </strong>
          </div>
          <div className="voice-boundary-menu__options" role="group" aria-label="Chế độ ranh giới">
            <button
              type="button"
              className={editingBoundarySegment.voiceBoundaryAfter.mode === 'AUTO' ? 'is-active' : ''}
              disabled={busy}
              onClick={() => applyBoundaryMode('AUTO')}
            >
              <RotateCcw size={14} />
              <span><strong>Tự động</strong><small>Để hệ thống quyết định theo nội dung.</small></span>
            </button>
            <button
              type="button"
              className={editingBoundarySegment.voiceBoundaryAfter.mode === 'JOIN' ? 'is-active is-join' : 'is-join'}
              disabled={busy || !editingBoundarySegment.voiceBoundaryAfter.canJoin}
              onClick={() => applyBoundaryMode('JOIN')}
            >
              <Link2 size={14} />
              <span><strong>Nói liền</strong><small>Gửi hai cue trong cùng một cụm TTS.</small></span>
            </button>
            <button
              type="button"
              className={editingBoundarySegment.voiceBoundaryAfter.mode === 'BREAK' ? 'is-active is-break' : 'is-break'}
              disabled={busy}
              onClick={() => applyBoundaryMode('BREAK')}
            >
              <Pause size={14} />
              <span><strong>Ngắt tại đây</strong><small>Bắt đầu một cụm giọng mới ở cue sau.</small></span>
            </button>
          </div>
          {editingBoundarySegment.voiceBoundaryAfter.constraintMessage ? (
            <p className="voice-boundary-menu__warning">
              <CircleAlert size={13} />
              {editingBoundarySegment.voiceBoundaryAfter.constraintMessage}
            </p>
          ) : (
            <p className="voice-boundary-menu__hint">
              Sau khi lưu, chỉ cụm bị ảnh hưởng cần tạo lại giọng.
            </p>
          )}
        </div>,
        document.body,
      ) : null}
      {editingSegment && editorPosition ? createPortal(
      <div
        ref={editorRef}
        className="timeline-subtitle-editor"
        role="dialog"
        aria-modal="false"
        aria-labelledby="timeline-subtitle-editor-title"
        style={editorPosition}
        onPointerDown={(event) => event.stopPropagation()}
        onKeyDown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault()
            closeEditor()
          }
        }}
      >
        <div className="timeline-subtitle-editor__header">
          <div>
            <span className="timeline-subtitle-editor__eyebrow">
              Phân đoạn {String(editingSegment.id).padStart(2, '0')}
            </span>
            <strong id="timeline-subtitle-editor-title">Sửa bản dịch tiếng Việt</strong>
            <time>
              {formatClock(editingSegment.start)} — {formatClock(editingSegment.end)}
            </time>
          </div>
          <button
            type="button"
            className="timeline-subtitle-editor__close"
            aria-label="Hủy chỉnh sửa"
            title="Hủy (Esc)"
            onClick={closeEditor}
          >
            <X size={16} />
          </button>
        </div>

        <label className="timeline-subtitle-editor__field">
          <span>Nội dung hiển thị và giọng đọc</span>
          <textarea
            ref={editorInputRef}
            rows={4}
            value={draftTranslation}
            placeholder="Nhập bản dịch tiếng Việt…"
            spellCheck
            disabled={busy}
            onChange={(event) => setDraftTranslation(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                event.preventDefault()
                saveEditor()
              }
            }}
          />
        </label>

        <div className="timeline-subtitle-editor__feedback" aria-live="polite">
          {readingEstimate?.tooLong ? (
            <span className="timeline-subtitle-editor__warning">
              <CircleAlert size={13} />
              Câu dài hơn thời lượng {readingEstimate.durationLabel}; giọng đọc có thể bị gấp.
            </span>
          ) : editingSegment.hasVoice
            && draftTranslation.trim() !== editingSegment.translated.trim() ? (
              <span className="timeline-subtitle-editor__warning">
                <CircleAlert size={13} />
                Audio cũ sẽ được đánh dấu cần tạo lại.
              </span>
            ) : (
              <span>Có thể xuống dòng. Ctrl + Enter để lưu.</span>
            )}
          <small>{draftTranslation.trim().length} ký tự</small>
        </div>

        <div className="timeline-subtitle-editor__actions">
          <button type="button" className="is-secondary" onClick={closeEditor}>
            <X size={14} />
            Hủy
          </button>
          <button type="button" className="is-primary" disabled={busy} onClick={saveEditor}>
            <Check size={14} />
            {busy ? 'Đang lưu…' : 'Lưu thay đổi'}
          </button>
        </div>
      </div>,
      document.body,
      ) : null}
    </>
  )
}

type EditorPosition = {
  top: number
  left: number
  width: number
}

function getEditorPosition(anchor: HTMLElement): EditorPosition {
  const viewportPadding = 12
  const editorGap = 8
  const estimatedEditorHeight = 290
  const rect = anchor.getBoundingClientRect()
  const width = Math.max(240, Math.min(440, window.innerWidth - viewportPadding * 2))
  const centeredLeft = rect.left + rect.width / 2 - width / 2
  const left = Math.max(
    viewportPadding,
    Math.min(centeredLeft, window.innerWidth - width - viewportPadding),
  )
  const hasRoomBelow = rect.bottom + editorGap + estimatedEditorHeight
    <= window.innerHeight - viewportPadding
  const top = hasRoomBelow
    ? rect.bottom + editorGap
    : Math.max(viewportPadding, rect.top - estimatedEditorHeight - editorGap)
  return { top, left, width }
}

type BoundaryPosition = {
  top: number
  left: number
  width: number
}

function getBoundaryPosition(anchor: HTMLElement): BoundaryPosition {
  const viewportPadding = 12
  const menuGap = 7
  const estimatedHeight = 245
  const rect = anchor.getBoundingClientRect()
  const width = Math.max(250, Math.min(300, window.innerWidth - viewportPadding * 2))
  const left = Math.max(
    viewportPadding,
    Math.min(rect.left + rect.width / 2 - width / 2, window.innerWidth - width - viewportPadding),
  )
  const top = rect.bottom + menuGap + estimatedHeight <= window.innerHeight - viewportPadding
    ? rect.bottom + menuGap
    : Math.max(viewportPadding, rect.top - estimatedHeight - menuGap)
  return { top, left, width }
}

function formatBoundaryLabel(mode: VoiceBoundaryMode, effectiveMode: 'JOIN' | 'BREAK') {
  if (mode === 'JOIN') return 'Nói liền'
  if (mode === 'BREAK') return 'Ngắt tại đây'
  return effectiveMode === 'JOIN' ? 'Tự động · đang nói liền' : 'Tự động · đang ngắt'
}

function getReadingEstimate(text: string, durationSeconds: number) {
  const normalized = text.trim()
  const words = normalized ? normalized.split(/\s+/u).length : 0
  const characters = normalized.replace(/\s/gu, '').length
  const estimatedSeconds = Math.max(words / 3, characters / 18)
  const safeDuration = Math.max(0.1, durationSeconds)
  return {
    tooLong: estimatedSeconds > safeDuration * 1.1,
    durationLabel: `${safeDuration.toFixed(safeDuration < 10 ? 1 : 0)} giây`,
  }
}

const TimelineFilmstrip = memo(function TimelineFilmstrip({
  thumbnails,
  duration,
  flipHorizontal,
  flipVertical,
}: {
  thumbnails: RenderedTimelineThumbnail[]
  duration: number
  flipHorizontal: boolean
  flipVertical: boolean
}) {
  return (
    <span className="video-filmstrip" aria-hidden="true">
      {thumbnails.map((thumbnail) => thumbnail.url ? (
        <img
          key={thumbnail.key}
          className={`video-filmstrip__frame ${thumbnail.isFallback ? 'is-fallback' : ''}`}
          src={thumbnail.url}
          alt=""
          draggable={false}
          style={{
            left: `${(thumbnail.start / duration) * 100}%`,
            width: `${((thumbnail.end - thumbnail.start) / duration) * 100}%`,
            transform: `scale(${flipHorizontal ? -1 : 1}, ${flipVertical ? -1 : 1})`,
          }}
        />
      ) : (
        <i
          key={thumbnail.key}
          className="video-filmstrip__placeholder"
          style={{
            left: `${(thumbnail.start / duration) * 100}%`,
            width: `${((thumbnail.end - thumbnail.start) / duration) * 100}%`,
          }}
        />
      ))}
    </span>
  )
})

function Waveform({ kind }: { kind: 'original' | 'voice' }) {
  return (
    <span className={`waveform waveform--${kind}`} aria-hidden="true">
      {Array.from({ length: 52 }, (_, index) => (
        <i
          key={index}
          style={{ height: `${24 + ((index * 17) % 64)}%` }}
        />
      ))}
    </span>
  )
}

function readPlayheadAnchorRatio() {
  try {
    const stored = Number.parseFloat(window.localStorage.getItem(playheadAnchorStorageKey) ?? '')
    if (Number.isFinite(stored)) {
      return Math.min(
        maximumPlayheadAnchorRatio,
        Math.max(minimumPlayheadAnchorRatio, stored),
      )
    }
  } catch {
    // Use the safe default when local storage is unavailable.
  }
  return defaultPlayheadAnchorRatio
}

function findNearestThumbnailUrl(
  target: TimelineThumbnailSample,
  samples: TimelineThumbnailSample[],
  cache: Map<string, string>,
) {
  let nearestUrl: string | null = null
  let nearestDistance = Number.POSITIVE_INFINITY
  for (const sample of samples) {
    const url = cache.get(sample.cacheKey)
    if (!url) continue
    const distance = Math.abs(sample.index - target.index)
    if (distance >= nearestDistance) continue
    nearestDistance = distance
    nearestUrl = url
  }
  return nearestUrl
}
