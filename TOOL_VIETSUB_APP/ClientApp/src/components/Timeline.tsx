import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
  Mic2,
  Pause,
  Pencil,
  Play,
  Scissors,
  Trash2,
  Volume2,
  VolumeX,
  X,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { formatClock } from '../lib/format'
import type { SubtitleSegment, VideoInfo } from '../types'
import { CompactRange, IconButton } from './Ui'

type TimelineProps = {
  video: VideoInfo | null
  flipHorizontal: boolean
  flipVertical: boolean
  segments: SubtitleSegment[]
  playing: boolean
  currentTime: number
  playbackRate: number
  sourceAudioEnabled: boolean
  sourceVolume: number
  sourceAudioAvailable: boolean
  voiceAudioEnabled: boolean
  voiceVolume: number
  voiceAudioAvailable: boolean
  selectedId: number | null
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
  onNotify: (title: string, description: string) => void
}

type TimelineViewport = {
  scrollLeft: number
  clientWidth: number
  scrollWidth: number
}

type TimelineThumbnailSample = {
  key: string
  time: number
  start: number
  end: number
}

const thumbnailTargetWidth = 112
const thumbnailOverscan = 2
const maximumCachedThumbnails = 160
const originalWaveformSampleCount = 512
const originalWaveformBarsPath = (() => {
  const centerY = 50
  const raw = Array.from({ length: originalWaveformSampleCount + 4 }, (_, index) => {
    const value = Math.sin((index + 1) * 12.9898 + 78.233) * 43758.5453
    return value - Math.floor(value)
  })
  return Array.from({ length: originalWaveformSampleCount }, (_, index) => {
    const x = index * 1000 / (originalWaveformSampleCount - 1)
    const detail = (
      raw[index] * 0.12
      + raw[index + 1] * 0.22
      + raw[index + 2] * 0.32
      + raw[index + 3] * 0.22
      + raw[index + 4] * 0.12
    )
    const rhythm = (
      Math.abs(Math.sin(index * 0.071 + 0.35)) * 0.34
      + Math.abs(Math.sin(index * 0.019 + 1.2)) * 0.22
      + Math.abs(Math.sin(index * 0.41 + 2.1)) * 0.12
    )
    const amplitude = 5.5 + Math.min(1, detail * 0.74 + rhythm * 0.42) * 27.5
    return `M${x.toFixed(2)} ${(centerY - amplitude).toFixed(2)}V${(centerY + amplitude).toFixed(2)}`
  }).join('')
})()

export function Timeline({
  video,
  flipHorizontal,
  flipVertical,
  segments,
  playing,
  currentTime,
  playbackRate,
  sourceAudioEnabled,
  sourceVolume,
  sourceAudioAvailable,
  voiceAudioEnabled,
  voiceVolume,
  voiceAudioAvailable,
  selectedId,
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
  onNotify,
}: TimelineProps) {
  const [timelineZoom, setTimelineZoom] = useState(0)
  const [bookmarks, setBookmarks] = useState<number[]>([])
  const [editingSegmentId, setEditingSegmentId] = useState<number | null>(null)
  const [draftTranslation, setDraftTranslation] = useState('')
  const [editorPosition, setEditorPosition] = useState<EditorPosition | null>(null)
  const [timelineViewport, setTimelineViewport] = useState<TimelineViewport>({
    scrollLeft: 0,
    clientWidth: 0,
    scrollWidth: 0,
  })
  const [thumbnailRevision, setThumbnailRevision] = useState(0)
  const timelineCanvasRef = useRef<HTMLDivElement>(null)
  const timelineContentRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<HTMLDivElement>(null)
  const editorInputRef = useRef<HTMLTextAreaElement>(null)
  const editingAnchorRef = useRef<HTMLButtonElement>(null)
  const thumbnailVideoRef = useRef<HTMLVideoElement | null>(null)
  const thumbnailReadyRef = useRef<Promise<void> | null>(null)
  const thumbnailSourceGenerationRef = useRef(0)
  const thumbnailGenerationQueueRef = useRef<Promise<void>>(Promise.resolve())
  const thumbnailCacheRef = useRef<Map<string, string>>(new Map())
  const desiredThumbnailKeysRef = useRef<Set<string>>(new Set())
  const duration = Math.max(video?.durationSeconds ?? 21, 0.001)
  const tickCount = 8
  const ticks = Array.from({ length: tickCount }, (_, index) =>
    index * duration / (tickCount - 1))
  const maximumZoomScale = Math.max(4, duration / 15)
  const timelineScale = 1 + (timelineZoom / 100) * (maximumZoomScale - 1)
  const timelineWidth = timelineScale * 100
  const detailZoom = timelineZoom >= 70
  const selectedSegment = segments.find((segment) => segment.id === selectedId) ?? null
  const editingSegment = segments.find((segment) => segment.id === editingSegmentId) ?? null
  const cueAtPlayhead = segments.find((segment) =>
    currentTime > segment.start + 0.1 && currentTime < segment.end - 0.1) ?? null
  const sourceAudioActive = sourceAudioAvailable && sourceAudioEnabled
  const voiceAudioActive = voiceAudioAvailable && voiceAudioEnabled
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

  const thumbnailSamples = useMemo(
    () => buildTimelineThumbnailSamples(duration, timelineViewport, thumbnailSourceKey),
    [duration, thumbnailSourceKey, timelineViewport],
  )

  const timelineThumbnails = useMemo(() => thumbnailSamples.map((sample) => ({
    ...sample,
    dataUrl: thumbnailCacheRef.current.get(sample.key) ?? null,
  })), [thumbnailRevision, thumbnailSamples])

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
    if (playing) onTogglePlay()
    onSelectSegment(segment.id)
    onSeek(segment.start)
    editingAnchorRef.current = anchor
    setEditingSegmentId(segment.id)
    setDraftTranslation(segment.translated)
    setEditorPosition(getEditorPosition(anchor))
  }, [busy, onSeek, onSelectSegment, onTogglePlay, playing])

  useEffect(() => {
    const canvas = timelineCanvasRef.current
    if (!canvas || timelineZoom === 0) return

    const frame = window.requestAnimationFrame(() => {
      const playheadPosition = Math.min(1, Math.max(0, currentTime / duration))
      const targetLeft = canvas.scrollWidth * playheadPosition - canvas.clientWidth / 2
      canvas.scrollLeft = Math.max(0, Math.min(targetLeft, canvas.scrollWidth - canvas.clientWidth))
    })

    return () => window.cancelAnimationFrame(frame)
  }, [duration, timelineZoom])

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
    const playbackUrl = video?.playbackUrl
    thumbnailSourceGenerationRef.current += 1
    const sourceGeneration = thumbnailSourceGenerationRef.current
    thumbnailCacheRef.current.clear()
    desiredThumbnailKeysRef.current.clear()
    setThumbnailRevision((value) => value + 1)

    const previousVideo = thumbnailVideoRef.current
    if (previousVideo) {
      previousVideo.removeAttribute('src')
      previousVideo.load()
    }
    thumbnailVideoRef.current = null
    thumbnailReadyRef.current = null
    thumbnailGenerationQueueRef.current = Promise.resolve()
    if (!playbackUrl) return

    const thumbnailVideo = document.createElement('video')
    thumbnailVideo.crossOrigin = 'anonymous'
    thumbnailVideo.preload = 'auto'
    thumbnailVideo.muted = true
    thumbnailVideo.playsInline = true
    thumbnailVideoRef.current = thumbnailVideo
    const thumbnailReady = waitForVideoMetadata(thumbnailVideo)
    // Mark the rejection as observed even if the timeline is hidden before it
    // requests its first frame. The generation queue still receives the same
    // rejected promise and falls back to the colored clip.
    void thumbnailReady.catch(() => undefined)
    thumbnailReadyRef.current = thumbnailReady
    thumbnailVideo.src = playbackUrl
    thumbnailVideo.load()

    return () => {
      if (thumbnailSourceGenerationRef.current === sourceGeneration) {
        thumbnailSourceGenerationRef.current += 1
      }
      thumbnailVideo.removeAttribute('src')
      thumbnailVideo.load()
      if (thumbnailVideoRef.current === thumbnailVideo) {
        thumbnailVideoRef.current = null
        thumbnailReadyRef.current = null
      }
    }
  }, [thumbnailSourceKey, video?.playbackUrl])

  useEffect(() => {
    const desiredKeys = new Set(thumbnailSamples.map((sample) => sample.key))
    desiredThumbnailKeysRef.current = desiredKeys
    const missingSamples = thumbnailSamples.filter(
      (sample) => !thumbnailCacheRef.current.has(sample.key),
    )
    if (missingSamples.length === 0 || !thumbnailVideoRef.current || !thumbnailReadyRef.current) {
      return
    }

    const sourceGeneration = thumbnailSourceGenerationRef.current
    thumbnailGenerationQueueRef.current = thumbnailGenerationQueueRef.current
      .catch(() => undefined)
      .then(async () => {
        const thumbnailVideo = thumbnailVideoRef.current
        const ready = thumbnailReadyRef.current
        if (!thumbnailVideo || !ready || sourceGeneration !== thumbnailSourceGenerationRef.current) return
        await ready

        for (const sample of missingSamples) {
          if (sourceGeneration !== thumbnailSourceGenerationRef.current) return
          if (!desiredThumbnailKeysRef.current.has(sample.key)
            || thumbnailCacheRef.current.has(sample.key)) continue
          const dataUrl = await captureVideoThumbnail(thumbnailVideo, sample.time)
          if (sourceGeneration !== thumbnailSourceGenerationRef.current) return
          thumbnailCacheRef.current.set(sample.key, dataUrl)
          pruneThumbnailCache(thumbnailCacheRef.current, desiredThumbnailKeysRef.current)
          setThumbnailRevision((value) => value + 1)
        }
      })
      .catch(() => {
        // Some codecs can be processed by FFmpeg but not decoded by WebView2.
        // Keep the normal colored clip as a safe fallback in that case.
      })
  }, [thumbnailSamples])

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
          label="Tốc độ phát"
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
          <div className={`track-label ${voiceAudioActive ? '' : 'is-muted'} ${voiceAudioAvailable ? '' : 'is-unavailable'}`}>
            <button
              type="button"
              className="track-audio-toggle"
              aria-label={voiceAudioActive ? 'Tắt Giọng Việt' : 'Bật Giọng Việt'}
              aria-pressed={voiceAudioActive}
              title={voiceAudioAvailable
                ? voiceAudioActive ? 'Tắt Giọng Việt' : 'Bật Giọng Việt'
                : 'Hãy tạo giọng Việt trước'}
              disabled={busy || !voiceAudioAvailable}
              onClick={onToggleVoiceAudio}
            >
              {voiceAudioActive ? <Volume2 size={14} /> : <VolumeX size={14} />}
            </button>
            <Mic2 size={15} />
            <strong>Giọng Việt</strong>
          </div>
        </div>

        <div
          ref={timelineCanvasRef}
          className="timeline-canvas"
          onScroll={updateTimelineViewport}
        >
          <div
            ref={timelineContentRef}
            className="timeline-scroll-content"
            style={{ width: `${timelineWidth}%` }}
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
                  <span className="video-filmstrip" aria-hidden="true">
                    {timelineThumbnails.map((thumbnail) => thumbnail.dataUrl ? (
                      <img
                        key={thumbnail.key}
                        className="video-filmstrip__frame"
                        src={thumbnail.dataUrl}
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
                  <span className="video-clip__label">
                    <Film size={13} />
                    <span>{video.fileName}</span>
                  </span>
                </div>
              ) : null}
            </div>
            <div className="timeline-track subtitle-track">
              {segments.map((segment) => {
                const displayText = segment.translated.trim()
                  || segment.original.trim()
                  || `Phân đoạn ${segment.id}`

                return (
                  <button
                    type="button"
                    key={segment.id}
                    className={`subtitle-clip ${currentTime >= segment.start && currentTime < segment.end ? 'is-current' : ''} ${selectedId === segment.id ? 'is-selected' : ''} ${editingSegmentId === segment.id ? 'is-editing' : ''}`}
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
                )
              })}
            </div>
            <div className={`timeline-track audio-track ${sourceAudioActive ? '' : 'is-muted'}`}>
              {video ? <Waveform kind="original" /> : null}
            </div>
            <div className={`timeline-track voice-track ${voiceAudioActive ? '' : 'is-muted'}`}>
              {segments.filter((segment) => segment.hasVoice).map((segment) => (
                <div
                  key={segment.id}
                  className="voice-clip"
                  style={{
                    left: `${(segment.start / duration) * 100}%`,
                    width: `${((segment.end - segment.start) / duration) * 100}%`,
                  }}
                >
                  <Waveform kind="voice" />
                </div>
              ))}
            </div>

            <div
              className="playhead"
              style={{ left: `${(Math.min(currentTime, duration) / duration) * 100}%` }}
              aria-hidden="true"
            >
              <span />
            </div>
          </div>
          </div>
        </div>
      </div>
      </section>
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

function Waveform({ kind }: { kind: 'original' | 'voice' }) {
  if (kind === 'original') {
    return (
      <span className="waveform waveform--original" aria-hidden="true">
        <svg
          className="waveform__continuous"
          viewBox="0 0 1000 100"
          preserveAspectRatio="none"
          focusable="false"
        >
          <defs>
            <linearGradient id="original-waveform-gradient" x1="0" y1="0" x2="0" y2="100" gradientUnits="userSpaceOnUse">
              <stop offset="0" stopColor="#376f9f" stopOpacity="0.58" />
              <stop offset="0.43" stopColor="#58b7ca" stopOpacity="0.86" />
              <stop offset="0.5" stopColor="#72c9d5" stopOpacity="0.94" />
              <stop offset="0.57" stopColor="#58b7ca" stopOpacity="0.86" />
              <stop offset="1" stopColor="#376f9f" stopOpacity="0.58" />
            </linearGradient>
          </defs>
          <line className="waveform__centerline" x1="0" y1="50" x2="1000" y2="50" />
          <path
            className="waveform__bars"
            d={originalWaveformBarsPath}
            stroke="url(#original-waveform-gradient)"
          />
        </svg>
      </span>
    )
  }

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

function buildTimelineThumbnailSamples(
  duration: number,
  viewport: TimelineViewport,
  sourceKey: string,
): TimelineThumbnailSample[] {
  if (!sourceKey || duration <= 0 || viewport.clientWidth <= 0 || viewport.scrollWidth <= 0) {
    return []
  }

  const scrollWidth = Math.max(viewport.clientWidth, viewport.scrollWidth)
  const visibleStart = Math.min(duration, Math.max(0, viewport.scrollLeft / scrollWidth * duration))
  const visibleEnd = Math.min(
    duration,
    Math.max(visibleStart, (viewport.scrollLeft + viewport.clientWidth) / scrollWidth * duration),
  )
  const visibleSpan = Math.max(duration / scrollWidth, visibleEnd - visibleStart)
  const visibleCellCount = Math.max(6, Math.min(14, Math.ceil(viewport.clientWidth / thumbnailTargetWidth)))
  const cellDuration = visibleSpan / visibleCellCount
  const firstCell = Math.max(0, Math.floor(visibleStart / cellDuration) - thumbnailOverscan)
  const lastCell = Math.min(
    Math.ceil(duration / cellDuration),
    Math.ceil(visibleEnd / cellDuration) + thumbnailOverscan,
  )
  const samples: TimelineThumbnailSample[] = []
  for (let index = firstCell; index < lastCell; index += 1) {
    const start = index * cellDuration
    const end = Math.min(duration, (index + 1) * cellDuration)
    if (end <= start) continue
    const time = Math.min(Math.max(0.001, (start + end) / 2), Math.max(0.001, duration - 0.001))
    samples.push({
      key: `${sourceKey}:${Math.round(time * 1000)}`,
      time,
      start,
      end,
    })
  }
  return samples
}

function waitForVideoMetadata(video: HTMLVideoElement): Promise<void> {
  if (video.readyState >= HTMLMediaElement.HAVE_METADATA) return Promise.resolve()
  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => finish(new Error('Quá thời gian đọc metadata video.')), 10_000)
    const finish = (error?: Error) => {
      window.clearTimeout(timeout)
      video.removeEventListener('loadedmetadata', handleLoaded)
      video.removeEventListener('error', handleError)
      if (error) reject(error)
      else resolve()
    }
    const handleLoaded = () => finish()
    const handleError = () => finish(new Error('WebView2 không đọc được video để tạo thumbnail.'))
    video.addEventListener('loadedmetadata', handleLoaded, { once: true })
    video.addEventListener('error', handleError, { once: true })
  })
}

async function captureVideoThumbnail(video: HTMLVideoElement, requestedTime: number) {
  const lastSafeTime = Number.isFinite(video.duration)
    ? Math.max(0.001, video.duration - 0.04)
    : requestedTime
  const time = Math.min(Math.max(0.001, requestedTime), lastSafeTime)
  await seekVideoForThumbnail(video, time)

  if (video.videoWidth <= 0 || video.videoHeight <= 0) {
    throw new Error('Khung hình video không hợp lệ.')
  }
  const canvas = document.createElement('canvas')
  canvas.width = 192
  canvas.height = 108
  const context = canvas.getContext('2d', { alpha: false })
  if (!context) throw new Error('Không thể tạo canvas thumbnail.')

  const sourceRatio = video.videoWidth / video.videoHeight
  const targetRatio = canvas.width / canvas.height
  let sourceX = 0
  let sourceY = 0
  let sourceWidth = video.videoWidth
  let sourceHeight = video.videoHeight
  if (sourceRatio > targetRatio) {
    sourceWidth = video.videoHeight * targetRatio
    sourceX = (video.videoWidth - sourceWidth) / 2
  } else if (sourceRatio < targetRatio) {
    sourceHeight = video.videoWidth / targetRatio
    sourceY = (video.videoHeight - sourceHeight) / 2
  }
  context.drawImage(
    video,
    sourceX,
    sourceY,
    sourceWidth,
    sourceHeight,
    0,
    0,
    canvas.width,
    canvas.height,
  )
  return canvas.toDataURL('image/jpeg', 0.72)
}

function seekVideoForThumbnail(video: HTMLVideoElement, time: number): Promise<void> {
  if (Math.abs(video.currentTime - time) < 0.015
    && video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
    return Promise.resolve()
  }

  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => finish(new Error('Quá thời gian lấy khung hình video.')), 8_000)
    const finish = (error?: Error) => {
      window.clearTimeout(timeout)
      video.removeEventListener('seeked', handleSeeked)
      video.removeEventListener('error', handleError)
      if (error) reject(error)
      else resolve()
    }
    const handleSeeked = () => finish()
    const handleError = () => finish(new Error('Không đọc được khung hình video.'))
    video.addEventListener('seeked', handleSeeked, { once: true })
    video.addEventListener('error', handleError, { once: true })
    try {
      video.currentTime = time
    } catch {
      finish(new Error('Không thể tua video để tạo thumbnail.'))
    }
  })
}

function pruneThumbnailCache(cache: Map<string, string>, desiredKeys: Set<string>) {
  if (cache.size <= maximumCachedThumbnails) return
  for (const key of cache.keys()) {
    if (!desiredKeys.has(key)) cache.delete(key)
    if (cache.size <= maximumCachedThumbnails) break
  }
}
