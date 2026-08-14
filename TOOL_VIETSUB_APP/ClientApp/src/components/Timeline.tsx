import { useCallback, useEffect, useRef, useState } from 'react'
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

export function Timeline({
  video,
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
  const timelineCanvasRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<HTMLDivElement>(null)
  const editorInputRef = useRef<HTMLTextAreaElement>(null)
  const editingAnchorRef = useRef<HTMLButtonElement>(null)
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

        <div ref={timelineCanvasRef} className="timeline-canvas">
          <div className="timeline-scroll-content" style={{ width: `${timelineWidth}%` }}>
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
                  <Film size={13} />
                  <span>{video.fileName}</span>
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
