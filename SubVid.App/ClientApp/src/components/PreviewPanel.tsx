import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type DragEvent,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react'
import {
  Crop,
  Ellipsis,
  Expand,
  Eye,
  EyeOff,
  FlipHorizontal2,
  FlipVertical2,
  Focus,
  Hand,
  Image,
  Maximize,
  MousePointer2,
  Palette,
  Pause,
  Play,
  Plus,
  ScanLine,
  Trash2,
  Type,
  UploadCloud,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { formatBytes } from '../lib/format'
import { normalizeSubtitleText } from '../lib/subtitleStyle'
import {
  createSubtitleRemovalRegion,
  getSubtitleRemovalRegions,
  maxSubtitleRemovalRegions,
  withSubtitleRemovalRegions,
} from '../lib/subtitleRemoval'
import type {
  SubtitleRemovalRegion,
  SubtitleRemovalSettings,
  SubtitleStyleSettings,
  VideoInfo,
  VideoTransformSettings,
} from '../types'
import { IconButton } from './Ui'

type PreviewPanelProps = {
  video: VideoInfo | null
  busy: boolean
  playing: boolean
  currentTime: number
  playbackRate: number
  originalAudioEnabled: boolean
  sourceVolume: number
  voiceAudioEnabled: boolean
  voiceVolume: number
  voicePlaybackUrl: string | null
  vietnameseSubtitlesEnabled: boolean
  subtitleText: string
  subtitleRemoval: SubtitleRemovalSettings
  subtitleStyle: SubtitleStyleSettings
  videoTransform: VideoTransformSettings
  onTogglePlay: () => void
  onTimeUpdate: (seconds: number) => void
  onPlaybackStateChange: (state: 'playing' | 'paused' | 'waiting') => void
  onPlaybackEnded: () => void
  onPlaybackError: () => void
  onVoicePlaybackError: () => void
  onVietnameseSubtitlesEnabledChange: (enabled: boolean) => void
  onOpenVideo: () => void
  onDropVideo: (file: File) => void
  onSubtitleRemovalChange: (settings: SubtitleRemovalSettings) => void
  onSubtitleStyleChange: (style: SubtitleStyleSettings) => void
  onVideoTransformChange: (settings: VideoTransformSettings) => void
}

type PreviewViewMode = 'fit' | 'fill' | 'actual'

type PreviewSize = {
  width: number
  height: number
}

type RemovalResizeEdges = {
  left: boolean
  right: boolean
  top: boolean
  bottom: boolean
}

const previewInset = 32
const minimumRemovalRegionWidth = 0.05
const minimumRemovalRegionHeight = 0.04

function getRemovalResizeEdges(
  clientX: number,
  clientY: number,
  element: HTMLElement,
): RemovalResizeEdges {
  const bounds = element.getBoundingClientRect()
  const hitArea = Math.min(14, Math.max(8, Math.min(bounds.width, bounds.height) / 3))
  const leftDistance = clientX - bounds.left
  const rightDistance = bounds.right - clientX
  const topDistance = clientY - bounds.top
  const bottomDistance = bounds.bottom - clientY
  const nearLeft = leftDistance <= hitArea
  const nearRight = rightDistance <= hitArea
  const nearTop = topDistance <= hitArea
  const nearBottom = bottomDistance <= hitArea

  return {
    left: nearLeft && (!nearRight || leftDistance <= rightDistance),
    right: nearRight && (!nearLeft || rightDistance < leftDistance),
    top: nearTop && (!nearBottom || topDistance <= bottomDistance),
    bottom: nearBottom && (!nearTop || bottomDistance < topDistance),
  }
}

function hasRemovalResizeEdge(edges: RemovalResizeEdges) {
  return edges.left || edges.right || edges.top || edges.bottom
}

function getRemovalCursor(edges: RemovalResizeEdges) {
  if ((edges.left && edges.top) || (edges.right && edges.bottom)) return 'nwse-resize'
  if ((edges.right && edges.top) || (edges.left && edges.bottom)) return 'nesw-resize'
  if (edges.left || edges.right) return 'ew-resize'
  if (edges.top || edges.bottom) return 'ns-resize'
  return 'move'
}

function hexToRgba(hex: string, alpha: number) {
  const value = hex.replace('#', '')
  if (!/^[0-9a-f]{6}$/i.test(value)) return `rgba(2, 6, 23, ${alpha})`
  const red = Number.parseInt(value.slice(0, 2), 16)
  const green = Number.parseInt(value.slice(2, 4), 16)
  const blue = Number.parseInt(value.slice(4, 6), 16)
  return `rgba(${red}, ${green}, ${blue}, ${Math.min(1, Math.max(0, alpha))})`
}

export function PreviewPanel({
  video,
  busy,
  playing,
  currentTime,
  playbackRate,
  originalAudioEnabled,
  sourceVolume,
  voiceAudioEnabled,
  voiceVolume,
  voicePlaybackUrl,
  vietnameseSubtitlesEnabled,
  subtitleText,
  subtitleRemoval,
  subtitleStyle,
  videoTransform,
  onTogglePlay,
  onTimeUpdate,
  onPlaybackStateChange,
  onPlaybackEnded,
  onPlaybackError,
  onVoicePlaybackError,
  onVietnameseSubtitlesEnabledChange,
  onOpenVideo,
  onDropVideo,
  onSubtitleRemovalChange,
  onSubtitleStyleChange,
  onVideoTransformChange,
}: PreviewPanelProps) {
  const [tool, setTool] = useState('pointer')
  const [zoom, setZoom] = useState(100)
  const [viewMode, setViewMode] = useState<PreviewViewMode>('fit')
  const [dragging, setDragging] = useState(false)
  const [playbackFailed, setPlaybackFailed] = useState(false)
  const [playControlVisible, setPlayControlVisible] = useState(true)
  const [compactToolsOpen, setCompactToolsOpen] = useState(false)
  const [stageSize, setStageSize] = useState<PreviewSize>({ width: 0, height: 0 })
  const [mediaSize, setMediaSize] = useState<PreviewSize | null>(null)
  const [draftRemoval, setDraftRemoval] = useState(subtitleRemoval)
  const [activeRemovalRegionId, setActiveRemovalRegionId] = useState<string | null>(
    getSubtitleRemovalRegions(subtitleRemoval)[0]?.id ?? null,
  )
  const [hoveredRemovalRegionId, setHoveredRemovalRegionId] = useState<string | null>(null)
  const [removalDropTargetActive, setRemovalDropTargetActive] = useState(false)
  const [pendingRemovalRegionId, setPendingRemovalRegionId] = useState<string | null>(null)
  const [draftSubtitleStyle, setDraftSubtitleStyle] = useState(subtitleStyle)
  const dragDepth = useRef(0)
  const stageRef = useRef<HTMLDivElement>(null)
  const playerRef = useRef<HTMLVideoElement>(null)
  const voicePlayerRef = useRef<HTMLAudioElement>(null)
  const playerShellRef = useRef<HTMLDivElement>(null)
  const playControlTimerRef = useRef<number | null>(null)
  const compactToolsRef = useRef<HTMLDivElement>(null)
  const draftRemovalRef = useRef(subtitleRemoval)
  const removalHoverExitTimerRef = useRef<number | null>(null)
  const removalDropTargetActiveRef = useRef(false)
  const draftSubtitleStyleRef = useRef(subtitleStyle)
  const removalInteraction = useRef<{
    mode: 'move' | 'resize'
    clientX: number
    clientY: number
    settings: SubtitleRemovalSettings
    region: SubtitleRemovalRegion
    resizeEdges: RemovalResizeEdges
  } | null>(null)
  const subtitleInteraction = useRef<{
    clientX: number
    clientY: number
    style: SubtitleStyleSettings
  } | null>(null)

  useEffect(() => {
    if (removalInteraction.current) return
    setDraftRemoval(subtitleRemoval)
    draftRemovalRef.current = subtitleRemoval
    const regions = getSubtitleRemovalRegions(subtitleRemoval)
    setActiveRemovalRegionId((current) => (
      current && regions.some((region) => region.id === current)
        ? current
        : regions[0]?.id ?? null
    ))
  }, [subtitleRemoval])

  useEffect(() => () => {
    if (removalHoverExitTimerRef.current !== null) {
      window.clearTimeout(removalHoverExitTimerRef.current)
    }
  }, [])

  useEffect(() => {
    if (!pendingRemovalRegionId) return

    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setPendingRemovalRegionId(null)
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [pendingRemovalRegionId])

  useEffect(() => {
    if (subtitleInteraction.current) return
    setDraftSubtitleStyle(subtitleStyle)
    draftSubtitleStyleRef.current = subtitleStyle
  }, [subtitleStyle])

  useEffect(() => {
    if (!compactToolsOpen) return

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target
      if (target instanceof Node && !compactToolsRef.current?.contains(target)) {
        setCompactToolsOpen(false)
      }
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setCompactToolsOpen(false)
    }

    document.addEventListener('pointerdown', handlePointerDown)
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [compactToolsOpen])

  useEffect(() => {
    if (!video || busy) setCompactToolsOpen(false)
  }, [busy, video])

  const revealPlayControl = useCallback(() => {
    if (playControlTimerRef.current !== null) {
      window.clearTimeout(playControlTimerRef.current)
      playControlTimerRef.current = null
    }

    setPlayControlVisible(true)
    if (playing) {
      playControlTimerRef.current = window.setTimeout(() => {
        setPlayControlVisible(false)
        playControlTimerRef.current = null
      }, 3000)
    }
  }, [playing])

  useEffect(() => {
    revealPlayControl()
    return () => {
      if (playControlTimerRef.current !== null) {
        window.clearTimeout(playControlTimerRef.current)
        playControlTimerRef.current = null
      }
    }
  }, [revealPlayControl, video?.playbackUrl])

  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return

    const updateStageSize = () => {
      const bounds = stage.getBoundingClientRect()
      const nextSize = {
        width: Math.max(0, bounds.width),
        height: Math.max(0, bounds.height),
      }

      setStageSize((current) => (
        Math.abs(current.width - nextSize.width) < 0.5 && Math.abs(current.height - nextSize.height) < 0.5
          ? current
          : nextSize
      ))
    }

    updateStageSize()
    const observer = new ResizeObserver(updateStageSize)
    observer.observe(stage)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const player = playerRef.current
    const voicePlayer = voicePlayerRef.current
    if (player) player.playbackRate = playbackRate
    if (voicePlayer) voicePlayer.playbackRate = playbackRate
  }, [playbackRate])

  useEffect(() => {
    const player = playerRef.current
    const voicePlayer = voicePlayerRef.current
    if (player) {
      player.muted = !originalAudioEnabled
      player.volume = Math.min(1, Math.max(0, sourceVolume / 100))
    }
    if (voicePlayer) {
      voicePlayer.muted = !voiceAudioEnabled
      voicePlayer.volume = Math.min(1, Math.max(0, voiceVolume / 100))
    }
  }, [originalAudioEnabled, sourceVolume, voiceAudioEnabled, voiceVolume])

  useEffect(() => {
    const player = playerRef.current
    const voicePlayer = voicePlayerRef.current
    if (player && Math.abs(player.currentTime - currentTime) > 0.2) {
      player.currentTime = currentTime
    }
    if (voicePlayer && Math.abs(voicePlayer.currentTime - currentTime) > 0.16) {
      voicePlayer.currentTime = currentTime
    }
  }, [currentTime, voicePlaybackUrl])

  useEffect(() => {
    const player = playerRef.current
    const voicePlayer = voicePlayerRef.current
    if (!player) return
    if (playing) {
      void player.play().catch(() => {
        setPlaybackFailed(true)
        onPlaybackError()
      })
      if (voicePlayer && voiceAudioEnabled && voicePlaybackUrl) {
        if (Math.abs(voicePlayer.currentTime - player.currentTime) > 0.08) {
          voicePlayer.currentTime = player.currentTime
        }
        void voicePlayer.play().catch(onVoicePlaybackError)
      }
    } else {
      player.pause()
      voicePlayer?.pause()
    }
  }, [playing, onPlaybackError, onVoicePlaybackError, voiceAudioEnabled, voicePlaybackUrl])

  useEffect(() => {
    setPlaybackFailed(false)
    setViewMode('fit')
    setZoom(100)
    setMediaSize(video?.width && video.height ? { width: video.width, height: video.height } : null)
  }, [video?.fileName, video?.height, video?.playbackUrl, video?.width])

  const sourceWidth = Math.max(1, mediaSize?.width ?? video?.width ?? 16)
  const sourceHeight = Math.max(1, mediaSize?.height ?? video?.height ?? 9)
  const hasMeasuredStage = stageSize.width > 0 && stageSize.height > 0
  const availableWidth = Math.max(1, stageSize.width - previewInset)
  const removalActionSpace = subtitleRemoval.enabled ? 54 : 0
  const availableHeight = Math.max(1, stageSize.height - previewInset - removalActionSpace)
  const fitScale = Math.min(availableWidth / sourceWidth, availableHeight / sourceHeight)
  const fillScale = Math.max(stageSize.width / sourceWidth, stageSize.height / sourceHeight)
  const baseScale = viewMode === 'fit' ? fitScale : viewMode === 'fill' ? fillScale : 1
  const renderedScale = baseScale * (zoom / 100)
  const renderedSize = {
    width: Math.max(1, sourceWidth * renderedScale),
    height: Math.max(1, sourceHeight * renderedScale),
  }
  const canvasSize = {
    width: viewMode === 'fill' ? stageSize.width : Math.max(stageSize.width, renderedSize.width + previewInset),
    height: viewMode === 'fill'
      ? stageSize.height
      : Math.max(stageSize.height, renderedSize.height + previewInset + removalActionSpace),
  }
  const playerStyle = {
    '--video-aspect-ratio': `${sourceWidth} / ${sourceHeight}`,
    ...(hasMeasuredStage ? { width: `${renderedSize.width}px`, height: `${renderedSize.height}px` } : {}),
  } as CSSProperties
  const canvasStyle = hasMeasuredStage
    ? { width: `${canvasSize.width}px`, height: `${canvasSize.height}px` }
    : undefined

  useEffect(() => {
    const stage = stageRef.current
    if (!stage || !hasMeasuredStage || viewMode === 'fill') return

    const animationFrame = requestAnimationFrame(() => {
      stage.scrollLeft = Math.max(0, (stage.scrollWidth - stage.clientWidth) / 2)
      stage.scrollTop = Math.max(0, (stage.scrollHeight - stage.clientHeight) / 2)
    })

    return () => cancelAnimationFrame(animationFrame)
  }, [hasMeasuredStage, renderedSize.height, renderedSize.width, viewMode, zoom])

  const selectViewMode = (mode: PreviewViewMode) => {
    setViewMode(mode)
    setZoom(100)
  }

  const handleDrop = (event: DragEvent) => {
    event.preventDefault()
    dragDepth.current = 0
    setDragging(false)
    const file = event.dataTransfer.files.item(0)
    if (file) onDropVideo(file)
  }

  const beginRemovalInteraction = (
    event: ReactPointerEvent<HTMLElement>,
    region: SubtitleRemovalRegion,
  ) => {
    if (!subtitleRemoval.enabled) return
    event.preventDefault()
    event.stopPropagation()
    event.currentTarget.setPointerCapture(event.pointerId)
    const resizeEdges = getRemovalResizeEdges(event.clientX, event.clientY, event.currentTarget)
    removalInteraction.current = {
      mode: hasRemovalResizeEdge(resizeEdges) ? 'resize' : 'move',
      clientX: event.clientX,
      clientY: event.clientY,
      settings: draftRemovalRef.current,
      region,
      resizeEdges,
    }
    removalDropTargetActiveRef.current = false
    setRemovalDropTargetActive(false)
    setActiveRemovalRegionId(region.id)
    setHoveredRemovalRegionId(region.id)
    setTool('scan')
  }

  const moveRemovalInteraction = (event: ReactPointerEvent<HTMLElement>) => {
    const interaction = removalInteraction.current
    const shell = playerShellRef.current
    if (!interaction) {
      event.currentTarget.style.cursor = getRemovalCursor(
        getRemovalResizeEdges(event.clientX, event.clientY, event.currentTarget),
      )
      return
    }
    if (!shell) return
    event.preventDefault()
    const bounds = shell.getBoundingClientRect()
    const isOverDropTarget = interaction.mode === 'move'
      && event.clientX >= bounds.left
      && event.clientX <= bounds.right
      && event.clientY >= bounds.bottom + 4
      && event.clientY <= bounds.bottom + 78
    removalDropTargetActiveRef.current = isOverDropTarget
    setRemovalDropTargetActive(isOverDropTarget)
    const deltaX = (event.clientX - interaction.clientX) / Math.max(1, bounds.width)
    const deltaY = (event.clientY - interaction.clientY) / Math.max(1, bounds.height)
    const nextRegion = interaction.mode === 'move'
      ? {
          ...interaction.region,
          x: Math.min(1 - interaction.region.width, Math.max(0, interaction.region.x + deltaX)),
          y: Math.min(1 - interaction.region.height, Math.max(0, interaction.region.y + deltaY)),
        }
      : (() => {
          const right = interaction.region.x + interaction.region.width
          const bottom = interaction.region.y + interaction.region.height
          const x = interaction.resizeEdges.left
            ? Math.min(right - minimumRemovalRegionWidth, Math.max(0, interaction.region.x + deltaX))
            : interaction.region.x
          const y = interaction.resizeEdges.top
            ? Math.min(bottom - minimumRemovalRegionHeight, Math.max(0, interaction.region.y + deltaY))
            : interaction.region.y
          return {
            ...interaction.region,
            x,
            y,
            width: interaction.resizeEdges.left
              ? right - x
              : interaction.resizeEdges.right
                ? Math.min(1 - interaction.region.x, Math.max(minimumRemovalRegionWidth, interaction.region.width + deltaX))
                : interaction.region.width,
            height: interaction.resizeEdges.top
              ? bottom - y
              : interaction.resizeEdges.bottom
                ? Math.min(1 - interaction.region.y, Math.max(minimumRemovalRegionHeight, interaction.region.height + deltaY))
                : interaction.region.height,
          }
        })()
    const next = withSubtitleRemovalRegions(
      interaction.settings,
      getSubtitleRemovalRegions(interaction.settings).map((region) => (
        region.id === interaction.region.id ? nextRegion : region
      )),
    )
    draftRemovalRef.current = next
    setDraftRemoval(next)
  }

  const finishRemovalInteraction = (event: ReactPointerEvent<HTMLElement>) => {
    const interaction = removalInteraction.current
    if (!interaction) return
    event.preventDefault()
    event.stopPropagation()
    removalInteraction.current = null
    if (removalDropTargetActiveRef.current && interaction.mode === 'move') {
      draftRemovalRef.current = interaction.settings
      setDraftRemoval(interaction.settings)
      setActiveRemovalRegionId(interaction.region.id)
      removalDropTargetActiveRef.current = false
      setRemovalDropTargetActive(false)
      setPendingRemovalRegionId(interaction.region.id)
      return
    }
    removalDropTargetActiveRef.current = false
    setRemovalDropTargetActive(false)
    onSubtitleRemovalChange(draftRemovalRef.current)
  }

  const handleRemovalKeyDown = (
    event: ReactKeyboardEvent<HTMLDivElement>,
    regionId: string,
  ) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return
    event.preventDefault()
    const step = event.shiftKey ? 0.02 : 0.005
    const horizontal = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0
    const vertical = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0
    const currentSettings = draftRemovalRef.current
    const currentRegions = getSubtitleRemovalRegions(currentSettings)
    const current = currentRegions.find((region) => region.id === regionId)
    if (!current) return
    const nextRegion = event.altKey
      ? {
          ...current,
          width: Math.min(1 - current.x, Math.max(0.05, current.width + horizontal)),
          height: Math.min(1 - current.y, Math.max(0.04, current.height + vertical)),
        }
      : {
          ...current,
          x: Math.min(1 - current.width, Math.max(0, current.x + horizontal)),
          y: Math.min(1 - current.height, Math.max(0, current.y + vertical)),
        }
    const next = withSubtitleRemovalRegions(
      currentSettings,
      currentRegions.map((region) => region.id === regionId ? nextRegion : region),
    )
    setActiveRemovalRegionId(regionId)
    draftRemovalRef.current = next
    setDraftRemoval(next)
    onSubtitleRemovalChange(next)
  }

  const addRemovalRegion = () => {
    const current = draftRemovalRef.current
    const regions = getSubtitleRemovalRegions(current)
    if (busy || regions.length >= maxSubtitleRemovalRegions) return
    const region = createSubtitleRemovalRegion(regions.length)
    const next = withSubtitleRemovalRegions(
      { ...current, enabled: true },
      [...regions, region],
    )
    draftRemovalRef.current = next
    setDraftRemoval(next)
    setActiveRemovalRegionId(region.id)
    setTool('scan')
    onSubtitleRemovalChange(next)
  }

  const requestRemovalConfirmation = (regionId: string) => {
    if (busy) return
    if (!getSubtitleRemovalRegions(draftRemovalRef.current).some((region) => region.id === regionId)) return
    setPendingRemovalRegionId(regionId)
  }

  const confirmRemovalRegion = () => {
    const regionId = pendingRemovalRegionId
    if (busy || !regionId) return
    const current = draftRemovalRef.current
    const nextRegions = getSubtitleRemovalRegions(current).filter((region) => region.id !== regionId)
    const next = withSubtitleRemovalRegions(
      { ...current, enabled: nextRegions.length > 0 && current.enabled },
      nextRegions,
    )
    draftRemovalRef.current = next
    setDraftRemoval(next)
    setActiveRemovalRegionId(nextRegions[0]?.id ?? null)
    setHoveredRemovalRegionId(null)
    setPendingRemovalRegionId(null)
    onSubtitleRemovalChange(next)
  }

  const clearRemovalHoverExit = () => {
    if (removalHoverExitTimerRef.current !== null) {
      window.clearTimeout(removalHoverExitTimerRef.current)
      removalHoverExitTimerRef.current = null
    }
  }

  const scheduleRemovalHoverExit = () => {
    clearRemovalHoverExit()
    removalHoverExitTimerRef.current = window.setTimeout(() => {
      setHoveredRemovalRegionId(null)
      removalHoverExitTimerRef.current = null
    }, 120)
  }

  const beginSubtitleInteraction = (event: ReactPointerEvent<HTMLDivElement>) => {
    event.preventDefault()
    event.stopPropagation()
    event.currentTarget.setPointerCapture(event.pointerId)
    subtitleInteraction.current = {
      clientX: event.clientX,
      clientY: event.clientY,
      style: draftSubtitleStyleRef.current,
    }
    setTool('text')
  }

  const moveSubtitleInteraction = (event: ReactPointerEvent<HTMLDivElement>) => {
    const interaction = subtitleInteraction.current
    const shell = playerShellRef.current
    if (!interaction || !shell) return
    event.preventDefault()
    const bounds = shell.getBoundingClientRect()
    const next: SubtitleStyleSettings = {
      ...interaction.style,
      presetId: 'custom',
      verticalPosition: 'custom',
      positionXPercent: Math.min(98, Math.max(2,
        interaction.style.positionXPercent + ((event.clientX - interaction.clientX) / Math.max(1, bounds.width) * 100))),
      positionYPercent: Math.min(98, Math.max(2,
        interaction.style.positionYPercent + ((event.clientY - interaction.clientY) / Math.max(1, bounds.height) * 100))),
    }
    draftSubtitleStyleRef.current = next
    setDraftSubtitleStyle(next)
  }

  const finishSubtitleInteraction = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!subtitleInteraction.current) return
    event.preventDefault()
    event.stopPropagation()
    subtitleInteraction.current = null
    onSubtitleStyleChange(draftSubtitleStyleRef.current)
  }

  const handleSubtitleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return
    event.preventDefault()
    const step = event.shiftKey ? 2 : 0.5
    const current = draftSubtitleStyleRef.current
    const next: SubtitleStyleSettings = {
      ...current,
      presetId: 'custom',
      verticalPosition: 'custom',
      positionXPercent: Math.min(98, Math.max(2, current.positionXPercent
        + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0))),
      positionYPercent: Math.min(98, Math.max(2, current.positionYPercent
        + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0))),
    }
    draftSubtitleStyleRef.current = next
    setDraftSubtitleStyle(next)
    onSubtitleStyleChange(next)
  }

  const draftRemovalRegions = getSubtitleRemovalRegions(draftRemoval)
  const hoveredRemovalRegion = draftRemovalRegions.find((region) => region.id === hoveredRemovalRegionId) ?? null
  const hoveredRemovalRegionIndex = hoveredRemovalRegion
    ? draftRemovalRegions.findIndex((region) => region.id === hoveredRemovalRegion.id)
    : -1

  const subtitleVerticalAnchor = draftSubtitleStyle.verticalPosition === 'custom'
    ? draftSubtitleStyle.positionYPercent < 34 ? 'top'
      : draftSubtitleStyle.positionYPercent > 66 ? 'bottom' : 'middle'
    : draftSubtitleStyle.verticalPosition
  const subtitleTranslateX = draftSubtitleStyle.horizontalAlignment === 'left'
    ? '0%' : draftSubtitleStyle.horizontalAlignment === 'right' ? '-100%' : '-50%'
  const subtitleTranslateY = subtitleVerticalAnchor === 'top'
    ? '0%' : subtitleVerticalAnchor === 'bottom' ? '-100%' : '-50%'
  const subtitleFontSize = Math.max(9, renderedSize.height * draftSubtitleStyle.fontSizePercent / 100)
  const subtitleOutline = draftSubtitleStyle.outlineSize * renderedSize.height / 360
  const subtitleShadow = draftSubtitleStyle.shadowSize * renderedSize.height / 360
  const subtitleStyleValue = {
    left: `${draftSubtitleStyle.positionXPercent}%`,
    top: `${draftSubtitleStyle.positionYPercent}%`,
    width: `${draftSubtitleStyle.maxWidthPercent}%`,
    transform: `translate(${subtitleTranslateX}, ${subtitleTranslateY})`,
    justifyContent: draftSubtitleStyle.horizontalAlignment === 'left'
      ? 'flex-start' : draftSubtitleStyle.horizontalAlignment === 'right' ? 'flex-end' : 'center',
    color: draftSubtitleStyle.textColor,
    fontFamily: `${draftSubtitleStyle.fontFamily}, Arial, sans-serif`,
    fontSize: `${subtitleFontSize}px`,
    fontWeight: draftSubtitleStyle.bold ? 700 : 400,
    textAlign: draftSubtitleStyle.horizontalAlignment,
  } as CSSProperties
  const subtitleTextStyle = {
    background: draftSubtitleStyle.backgroundMode === 'box'
      ? hexToRgba(draftSubtitleStyle.backgroundColor, draftSubtitleStyle.backgroundOpacity / 100)
      : 'transparent',
    WebkitTextStroke: subtitleOutline > 0
      ? `${subtitleOutline}px ${draftSubtitleStyle.outlineColor}`
      : undefined,
    textShadow: subtitleShadow > 0
      ? `${subtitleShadow}px ${subtitleShadow}px ${Math.max(0.5, subtitleShadow * 0.5)}px rgba(0, 0, 0, 0.9)`
      : 'none',
  } as CSSProperties

  return (
    <section
      className={`panel preview-panel ${dragging ? 'is-dragging' : ''}`}
      aria-label="Xem trước video"
      onDragEnter={(event) => {
        event.preventDefault()
        dragDepth.current += 1
        setDragging(true)
      }}
      onDragLeave={(event) => {
        event.preventDefault()
        dragDepth.current -= 1
        if (dragDepth.current <= 0) setDragging(false)
      }}
      onDragOver={(event) => event.preventDefault()}
      onDrop={handleDrop}
    >
      <div className="preview-toolbar">
        <div className="preview-toolbar__left">
          <div className="preview-toolbar__expanded-tools">
          <IconButton
            label="Cắt khung hình"
            size="small"
            className="preview-primary-tool preview-primary-tool--crop"
          >
            <Crop size={16} />
          </IconButton>
          <IconButton
            label="Lật ngang"
            size="small"
            className="video-flip-button preview-primary-tool preview-primary-tool--flip-horizontal"
            active={videoTransform.flipHorizontal}
            aria-pressed={videoTransform.flipHorizontal}
            disabled={!video || busy}
            onClick={() => onVideoTransformChange({
              ...videoTransform,
              flipHorizontal: !videoTransform.flipHorizontal,
            })}
          >
            <FlipHorizontal2 size={16} />
          </IconButton>
          <IconButton
            label="Lật dọc"
            size="small"
            className="video-flip-button preview-primary-tool preview-primary-tool--flip-vertical"
            active={videoTransform.flipVertical}
            aria-pressed={videoTransform.flipVertical}
            disabled={!video || busy}
            onClick={() => onVideoTransformChange({
              ...videoTransform,
              flipVertical: !videoTransform.flipVertical,
            })}
          >
            <FlipVertical2 size={16} />
          </IconButton>
          <span
            className={`source-pill preview-primary-tool preview-primary-tool--source-status ${subtitleText.trim() && !subtitleRemoval.enabled ? 'source-pill--warning' : ''}`}
            title={subtitleText.trim() && !subtitleRemoval.enabled
              ? 'Phụ đề gốc vẫn còn hiển thị. Bật Xóa phụ đề gốc để tránh chữ chồng lên nhau.'
              : undefined}
          >
            <span className="source-pill__text">
              {subtitleRemoval.enabled
                ? `Xóa sub · ${subtitleRemoval.mode === 'blur' ? 'Làm mờ' : 'Nền tối'}`
                : subtitleText.trim() ? 'Sub gốc chưa xóa' : 'Original'}
            </span>
          </span>
          <IconButton
            label={vietnameseSubtitlesEnabled ? 'Ẩn phụ đề Việt' : 'Hiện phụ đề Việt'}
            size="small"
            className="vietnamese-subtitle-visibility preview-primary-tool preview-primary-tool--subtitle-visibility"
            active={vietnameseSubtitlesEnabled}
            aria-pressed={vietnameseSubtitlesEnabled}
            onClick={() => onVietnameseSubtitlesEnabledChange(!vietnameseSubtitlesEnabled)}
          >
            {vietnameseSubtitlesEnabled ? <Eye size={16} /> : <EyeOff size={16} />}
          </IconButton>
          </div>

          <div ref={compactToolsRef} className="preview-toolbar__compact-tools">
            <IconButton
              label="Công cụ video"
              size="small"
              className="compact-tools-trigger"
              active={compactToolsOpen}
              aria-haspopup="menu"
              aria-expanded={compactToolsOpen}
              onClick={() => setCompactToolsOpen((current) => !current)}
            >
              <Ellipsis size={18} />
            </IconButton>

            {compactToolsOpen ? (
              <div className="compact-tools-popover" role="menu" aria-label="Công cụ video">
                <div
                  className={`compact-tools-status compact-overflow-item compact-overflow-item--source-status ${subtitleText.trim() && !subtitleRemoval.enabled ? 'is-warning' : ''}`}
                  role="status"
                >
                  <span>Trạng thái sub gốc</span>
                  <strong>
                    {subtitleRemoval.enabled
                      ? `Đã xóa · ${subtitleRemoval.mode === 'blur' ? 'Làm mờ' : 'Nền tối'}`
                      : subtitleText.trim() ? 'Chưa xóa' : 'Original'}
                  </strong>
                </div>
                <button
                  type="button"
                  className="compact-tool-item compact-overflow-item compact-overflow-item--crop"
                  role="menuitem"
                  disabled={!video || busy}
                >
                  <Crop size={16} />
                  <span>Cắt khung hình</span>
                </button>
                <button
                  type="button"
                  className={`compact-tool-item compact-overflow-item compact-overflow-item--flip-horizontal ${videoTransform.flipHorizontal ? 'is-active' : ''}`}
                  role="menuitemcheckbox"
                  aria-checked={videoTransform.flipHorizontal}
                  disabled={!video || busy}
                  onClick={() => {
                    onVideoTransformChange({
                      ...videoTransform,
                      flipHorizontal: !videoTransform.flipHorizontal,
                    })
                    setCompactToolsOpen(false)
                  }}
                >
                  <FlipHorizontal2 size={16} />
                  <span>Lật ngang</span>
                </button>
                <button
                  type="button"
                  className={`compact-tool-item compact-overflow-item compact-overflow-item--flip-vertical ${videoTransform.flipVertical ? 'is-active' : ''}`}
                  role="menuitemcheckbox"
                  aria-checked={videoTransform.flipVertical}
                  disabled={!video || busy}
                  onClick={() => {
                    onVideoTransformChange({
                      ...videoTransform,
                      flipVertical: !videoTransform.flipVertical,
                    })
                    setCompactToolsOpen(false)
                  }}
                >
                  <FlipVertical2 size={16} />
                  <span>Lật dọc</span>
                </button>
                <button
                  type="button"
                  className={`compact-tool-item compact-overflow-item compact-overflow-item--subtitle-visibility ${vietnameseSubtitlesEnabled ? 'is-active' : ''}`}
                  role="menuitemcheckbox"
                  aria-checked={vietnameseSubtitlesEnabled}
                  disabled={!video || busy}
                  onClick={() => {
                    onVietnameseSubtitlesEnabledChange(!vietnameseSubtitlesEnabled)
                    setCompactToolsOpen(false)
                  }}
                >
                  {vietnameseSubtitlesEnabled ? <Eye size={16} /> : <EyeOff size={16} />}
                  <span>{vietnameseSubtitlesEnabled ? 'Ẩn phụ đề Việt' : 'Hiện phụ đề Việt'}</span>
                </button>
              </div>
            ) : null}
          </div>
        </div>

        <div className="zoom-control" aria-label="Mức thu phóng">
          <IconButton label="Thu nhỏ" size="small" onClick={() => setZoom((value) => Math.max(50, value - 10))}>
            <ZoomOut size={16} />
          </IconButton>
          <output aria-live="polite">{zoom}%</output>
          <IconButton label="Phóng to" size="small" onClick={() => setZoom((value) => Math.min(200, value + 10))}>
            <ZoomIn size={16} />
          </IconButton>
          <span className="zoom-control__divider" aria-hidden="true" />
          <IconButton
            label="Vừa khung"
            size="small"
            active={viewMode === 'fit'}
            aria-pressed={viewMode === 'fit'}
            onClick={() => selectViewMode('fit')}
          >
            <Focus size={16} />
          </IconButton>
          <IconButton
            label="Lấp đầy khung"
            size="small"
            active={viewMode === 'fill'}
            aria-pressed={viewMode === 'fill'}
            onClick={() => selectViewMode('fill')}
          >
            <Expand size={15} />
          </IconButton>
          <IconButton
            label="Kích thước thật 1:1"
            size="small"
            className="actual-size-button"
            active={viewMode === 'actual'}
            aria-pressed={viewMode === 'actual'}
            onClick={() => selectViewMode('actual')}
          >
            <span aria-hidden="true">1:1</span>
          </IconButton>
        </div>

        <div className="editing-tools" role="toolbar" aria-label="Công cụ chỉnh sửa">
          {[
            { id: 'pointer', label: 'Chọn', Icon: MousePointer2 },
            { id: 'hand', label: 'Di chuyển', Icon: Hand },
            { id: 'text', label: 'Văn bản', Icon: Type },
            { id: 'scan', label: 'Vùng xóa phụ đề gốc', Icon: ScanLine },
            { id: 'frame', label: 'Khung', Icon: Maximize },
            { id: 'image', label: 'Ảnh', Icon: Image },
            { id: 'color', label: 'Màu sắc', Icon: Palette },
          ].map(({ id, label, Icon }) => (
            <IconButton
              key={id}
              label={label}
              size="small"
              className={`editing-tool editing-tool--${id}`}
              active={tool === id}
              onClick={() => setTool(id)}
            >
              <Icon size={16} />
            </IconButton>
          ))}
          <IconButton
            label={draftRemovalRegions.length >= maxSubtitleRemovalRegions
              ? `Đã đạt tối đa ${maxSubtitleRemovalRegions} vùng che`
              : 'Thêm vùng che'}
            size="small"
            className="editing-tool editing-tool--add-removal"
            disabled={busy || draftRemovalRegions.length >= maxSubtitleRemovalRegions}
            onClick={addRemovalRegion}
          >
            <Plus size={16} />
          </IconButton>
          <IconButton
            label="Toàn màn hình"
            size="small"
            className="editing-tool editing-tool--fullscreen"
          >
            <Expand size={16} />
          </IconButton>
        </div>
      </div>

      <div
        ref={stageRef}
        className={`preview-stage preview-stage--${viewMode} ${video?.playbackUrl ? 'has-player' : ''}`}
        aria-label={video ? `Khung xem trước: ${viewMode === 'fit' ? 'vừa khung' : viewMode === 'fill' ? 'lấp đầy' : 'kích thước thật'}` : undefined}
      >
        {video ? (
          video.playbackUrl ? (
            <div className="preview-canvas" style={canvasStyle}>
              <div className="preview-player-stack" style={playerStyle}>
                <div
                  ref={playerShellRef}
                  className="video-player-shell"
                  onPointerEnter={revealPlayControl}
                  onPointerMove={revealPlayControl}
                  onPointerDown={revealPlayControl}
                >
                <video
                  ref={playerRef}
                  className="preview-video"
                  style={{
                    transform: `scale(${videoTransform.flipHorizontal ? -1 : 1}, ${videoTransform.flipVertical ? -1 : 1})`,
                  }}
                  src={video.playbackUrl}
                  preload="metadata"
                  playsInline
                  aria-label={`Video ${video.fileName}`}
                  onLoadedMetadata={(event) => {
                    const player = event.currentTarget
                    if (player.videoWidth > 0 && player.videoHeight > 0) {
                      setMediaSize({ width: player.videoWidth, height: player.videoHeight })
                    }
                    setPlaybackFailed(false)
                  }}
                  onTimeUpdate={(event) => onTimeUpdate(event.currentTarget.currentTime)}
                  onPlaying={() => onPlaybackStateChange('playing')}
                  onPause={() => onPlaybackStateChange('paused')}
                  onWaiting={() => onPlaybackStateChange('waiting')}
                  onSeeking={() => onPlaybackStateChange('waiting')}
                  onSeeked={(event) => onPlaybackStateChange(
                    event.currentTarget.paused ? 'paused' : 'playing',
                  )}
                  onEnded={onPlaybackEnded}
                  onError={() => {
                    setPlaybackFailed(true)
                    onPlaybackError()
                  }}
                />
                {voicePlaybackUrl ? (
                  <audio
                    ref={voicePlayerRef}
                    src={voicePlaybackUrl}
                    preload="auto"
                    aria-hidden="true"
                    onLoadedMetadata={(event) => {
                      const voicePlayer = event.currentTarget
                      voicePlayer.currentTime = Math.min(currentTime, voicePlayer.duration || currentTime)
                    }}
                    onError={onVoicePlaybackError}
                  />
                ) : null}
                {subtitleRemoval.enabled ? draftRemovalRegions.map((region, index) => (
                  <div
                    key={region.id}
                    className={`subtitle-removal-region subtitle-removal-region--${subtitleRemoval.mode} ${activeRemovalRegionId === region.id ? 'is-active' : ''}`}
                    style={{
                      left: `${region.x * 100}%`,
                      top: `${region.y * 100}%`,
                      width: `${region.width * 100}%`,
                      height: `${region.height * 100}%`,
                    } as CSSProperties}
                    role="slider"
                    tabIndex={0}
                    aria-label="Vùng xóa phụ đề Trung. Dùng phím mũi tên để di chuyển, Alt cộng phím mũi tên để đổi kích thước."
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.round(region.y * 100)}
                    aria-valuetext={`X ${Math.round(region.x * 100)}%, Y ${Math.round(region.y * 100)}%, rộng ${Math.round(region.width * 100)}%, cao ${Math.round(region.height * 100)}%`}
                    onFocus={() => setActiveRemovalRegionId(region.id)}
                    onPointerEnter={() => {
                      clearRemovalHoverExit()
                      setHoveredRemovalRegionId(region.id)
                      setActiveRemovalRegionId(region.id)
                    }}
                    onPointerLeave={(event) => {
                      event.currentTarget.style.cursor = 'move'
                      scheduleRemovalHoverExit()
                    }}
                    onKeyDown={(event) => handleRemovalKeyDown(event, region.id)}
                    onPointerDown={(event) => beginRemovalInteraction(event, region)}
                    onPointerMove={moveRemovalInteraction}
                    onPointerUp={finishRemovalInteraction}
                    onPointerCancel={finishRemovalInteraction}
                  >
                    <span>Vùng che {index + 1}</span>
                  </div>
                )) : null}
                {vietnameseSubtitlesEnabled && subtitleText.trim() ? (
                  <div
                    className={`preview-vietnamese-subtitle ${subtitleRemoval.enabled ? '' : 'is-source-visible'} ${subtitleInteraction.current ? 'is-dragging' : ''}`}
                    style={subtitleStyleValue}
                    role="group"
                    tabIndex={0}
                    aria-label="Phụ đề Việt. Kéo để đổi vị trí hoặc dùng phím mũi tên."
                    aria-live="polite"
                    onKeyDown={handleSubtitleKeyDown}
                    onPointerDown={beginSubtitleInteraction}
                    onPointerMove={moveSubtitleInteraction}
                    onPointerUp={finishSubtitleInteraction}
                    onPointerCancel={finishSubtitleInteraction}
                  >
                    <span className="preview-vietnamese-subtitle__text" style={subtitleTextStyle}>
                      {normalizeSubtitleText(subtitleText)}
                    </span>
                  </div>
                ) : null}
                <div className={`video-player-overlay ${playControlVisible ? 'is-visible' : ''}`}>
                  <span className="video-player-name">{video.fileName}</span>
                </div>
                <button
                  type="button"
                  className={`preview-play preview-play--real ${playing ? 'is-playing' : ''} ${playControlVisible ? 'is-visible' : ''}`}
                  aria-label={playing ? 'Tạm dừng' : 'Phát video'}
                  onClick={onTogglePlay}
                >
                  {playing ? <Pause size={22} fill="currentColor" /> : <Play size={24} fill="currentColor" />}
                </button>
                {playbackFailed ? (
                  <div className="video-playback-error" role="alert">
                    Codec này chưa phát được trong WebView2. FFmpeg vẫn có thể xử lý video.
                  </div>
                ) : null}
                </div>
                {subtitleRemoval.enabled ? (
                  <div
                    className={`subtitle-removal-actions ${hoveredRemovalRegion || removalDropTargetActive ? 'is-visible' : ''} ${removalDropTargetActive ? 'is-drop-target' : ''}`}
                    onPointerEnter={clearRemovalHoverExit}
                    onPointerLeave={scheduleRemovalHoverExit}
                  >
                    {removalDropTargetActive ? (
                      <span>Thả để xoá vùng che</span>
                    ) : (
                      <button
                        type="button"
                        disabled={busy || !hoveredRemovalRegion}
                        onClick={() => hoveredRemovalRegion && requestRemovalConfirmation(hoveredRemovalRegion.id)}
                      >
                        <Trash2 size={14} aria-hidden="true" />
                        Xóa vùng che {hoveredRemovalRegionIndex + 1}
                      </button>
                    )}
                  </div>
                ) : null}
              </div>
            </div>
          ) : (
            <div className="video-mock" style={{ transform: `scale(${zoom / 100})` }}>
              <div className="video-mock__glow" />
              <div className="video-mock__grid" />
              <div className="video-mock__content">
                <span className="video-chip">{video.extension}</span>
                <h2>{video.fileName}</h2>
                <p>
                  {formatBytes(video.sizeBytes)}
                  {video.width && video.height ? ` · ${video.width}×${video.height}` : ''}
                  {video.framesPerSecond ? ` · ${video.framesPerSecond.toFixed(2)} FPS` : ''}
                  {video.hasAudio === false ? ' · Không có audio' : ''}
                </p>
                <button type="button" className={`preview-play ${playing ? 'is-playing' : ''}`} aria-label={playing ? 'Tạm dừng' : 'Phát video'} onClick={onTogglePlay}>
                  {playing ? <Pause size={22} fill="currentColor" /> : <Play size={24} fill="currentColor" />}
                </button>
              </div>
            </div>
          )
        ) : (
          <button type="button" className="video-dropzone" onClick={onOpenVideo}>
            <span className="video-dropzone__icon"><UploadCloud size={28} /></span>
            <strong>{dragging ? 'Thả video để nhập' : 'Nhấn hoặc kéo video vào đây'}</strong>
            <span>Bắt đầu tạo phụ đề và giọng Việt</span>
            <small>MP4 · MKV · MOV · WEBM</small>
          </button>
        )}
      </div>

      {dragging ? (
        <div className="drop-overlay" aria-hidden="true">
          <UploadCloud size={36} />
          <strong>Thả video để bắt đầu</strong>
        </div>
      ) : null}
      {pendingRemovalRegionId ? (
        <div
          className="subtitle-removal-confirm-backdrop"
          role="presentation"
          onPointerDown={(event) => {
            if (event.target === event.currentTarget) setPendingRemovalRegionId(null)
          }}
        >
          <section
            className="subtitle-removal-confirm-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="subtitle-removal-confirm-title"
          >
            <Trash2 size={20} aria-hidden="true" />
            <div>
              <h2 id="subtitle-removal-confirm-title">Xóa vùng che?</h2>
              <p>Vùng che này sẽ không còn được áp dụng trên preview và khi xuất video.</p>
            </div>
            <div className="subtitle-removal-confirm-dialog__actions">
              <button type="button" autoFocus onClick={() => setPendingRemovalRegionId(null)}>Hủy</button>
              <button type="button" className="is-danger" disabled={busy} onClick={confirmRemovalRegion}>Xóa vùng che</button>
            </div>
          </section>
        </div>
      ) : null}
    </section>
  )
}
