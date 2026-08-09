import { useState } from 'react'
import {
  AlignLeft,
  Bookmark,
  Captions,
  Copy,
  Eye,
  Film,
  Layers3,
  Mic2,
  Pause,
  Play,
  Scissors,
  Trash2,
  Volume2,
  VolumeX,
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
  onTogglePlay: () => void
  onSeek: (seconds: number) => void
  onNotify: (title: string, description: string) => void
}

const duration = 21
const ticks = Array.from({ length: 8 }, (_, index) => index * 3)

export function Timeline({
  video,
  segments,
  playing,
  currentTime,
  onTogglePlay,
  onSeek,
  onNotify,
}: TimelineProps) {
  const [playbackRate, setPlaybackRate] = useState(1)
  const [sourceVolume, setSourceVolume] = useState(70)
  const [voiceVolume, setVoiceVolume] = useState(78)
  const [timelineZoom, setTimelineZoom] = useState(55)

  return (
    <section className="timeline-section" aria-label="Dòng thời gian">
      <div className="transport-bar">
        <time className="main-timecode">{formatClock(currentTime, true)}</time>

        <div className="transport-tools" role="toolbar" aria-label="Công cụ timeline">
          <IconButton label="Cắt tại playhead" size="small"><Scissors size={16} /></IconButton>
          <IconButton label="Căn trái" size="small"><AlignLeft size={16} /></IconButton>
          <IconButton label="Sao chép" size="small"><Copy size={16} /></IconButton>
          <IconButton label="Xóa" size="small"><Trash2 size={16} /></IconButton>
          <IconButton label="Đánh dấu" size="small"><Bookmark size={16} /></IconButton>
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
          onChange={setPlaybackRate}
        />
        <CompactRange
          label="Âm lượng gốc"
          value={sourceVolume}
          icon={<VolumeX size={16} />}
          onChange={setSourceVolume}
        />
        <CompactRange
          label="Âm lượng giọng Việt"
          value={voiceVolume}
          icon={<Mic2 size={16} />}
          onChange={setVoiceVolume}
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
          <div className="track-label">
            <span><Volume2 size={14} /></span>
            <Volume2 size={15} />
            <strong>Âm gốc</strong>
          </div>
          <div className="track-label">
            <span><Volume2 size={14} /></span>
            <Mic2 size={15} />
            <strong>Giọng Việt</strong>
          </div>
        </div>

        <div className="timeline-canvas">
          <div
            className="timeline-ruler"
            role="slider"
            tabIndex={0}
            aria-label="Vị trí phát trên dòng thời gian"
            aria-valuemin={0}
            aria-valuemax={duration}
            aria-valuenow={Math.round(currentTime * 10) / 10}
            onPointerDown={(event) => {
              const rect = event.currentTarget.getBoundingClientRect()
              onSeek(((event.clientX - rect.left) / rect.width) * duration)
            }}
            onKeyDown={(event) => {
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
              {segments.map((segment) => (
                <div
                  key={segment.id}
                  className="subtitle-clip"
                  style={{
                    left: `${(segment.start / duration) * 100}%`,
                    width: `${((segment.end - segment.start) / duration) * 100}%`,
                  }}
                  title={segment.translated}
                >
                  <span>{segment.translated}</span>
                </div>
              ))}
            </div>
            <div className="timeline-track audio-track">
              {video ? <Waveform kind="original" /> : null}
            </div>
            <div className="timeline-track voice-track">
              {segments.map((segment) => (
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
    </section>
  )
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
