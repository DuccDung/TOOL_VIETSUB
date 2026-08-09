import { useRef, useState, type DragEvent } from 'react'
import {
  Crop,
  Expand,
  Focus,
  Hand,
  Image,
  Maximize,
  MousePointer2,
  Palette,
  Play,
  ScanLine,
  Type,
  UploadCloud,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { formatBytes } from '../lib/format'
import type { VideoInfo } from '../types'
import { IconButton } from './Ui'

type PreviewPanelProps = {
  video: VideoInfo | null
  playing: boolean
  onTogglePlay: () => void
  onOpenVideo: () => void
  onDropVideo: (file: File) => void
}

export function PreviewPanel({
  video,
  playing,
  onTogglePlay,
  onOpenVideo,
  onDropVideo,
}: PreviewPanelProps) {
  const [tool, setTool] = useState('pointer')
  const [zoom, setZoom] = useState(100)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)

  const handleDrop = (event: DragEvent) => {
    event.preventDefault()
    dragDepth.current = 0
    setDragging(false)
    const file = event.dataTransfer.files.item(0)
    if (file) onDropVideo(file)
  }

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
          <IconButton label="Cắt khung hình" size="small">
            <Crop size={16} />
          </IconButton>
          <span className="source-pill">Original</span>
        </div>

        <div className="zoom-control" aria-label="Mức thu phóng">
          <IconButton
            label="Thu nhỏ"
            size="small"
            onClick={() => setZoom((value) => Math.max(50, value - 10))}
          >
            <ZoomOut size={16} />
          </IconButton>
          <span>{zoom}%</span>
          <IconButton
            label="Phóng to"
            size="small"
            onClick={() => setZoom((value) => Math.min(200, value + 10))}
          >
            <ZoomIn size={16} />
          </IconButton>
          <IconButton label="Vừa khung" size="small" onClick={() => setZoom(100)}>
            <Focus size={16} />
          </IconButton>
        </div>

        <div className="editing-tools" role="toolbar" aria-label="Công cụ chỉnh sửa">
          {[
            { id: 'pointer', label: 'Chọn', Icon: MousePointer2 },
            { id: 'hand', label: 'Di chuyển', Icon: Hand },
            { id: 'text', label: 'Văn bản', Icon: Type },
            { id: 'scan', label: 'Vùng OCR', Icon: ScanLine },
            { id: 'frame', label: 'Khung', Icon: Maximize },
            { id: 'image', label: 'Ảnh', Icon: Image },
            { id: 'color', label: 'Màu sắc', Icon: Palette },
          ].map(({ id, label, Icon }) => (
            <IconButton
              key={id}
              label={label}
              size="small"
              active={tool === id}
              onClick={() => setTool(id)}
            >
              <Icon size={16} />
            </IconButton>
          ))}
          <IconButton label="Toàn màn hình" size="small">
            <Expand size={16} />
          </IconButton>
        </div>
      </div>

      <div className="preview-stage">
        {video ? (
          <div className="video-mock" style={{ transform: `scale(${zoom / 100})` }}>
            <div className="video-mock__glow" />
            <div className="video-mock__grid" />
            <div className="video-mock__content">
              <span className="video-chip">{video.extension}</span>
              <h2>{video.fileName}</h2>
              <p>{formatBytes(video.sizeBytes)} · Sẵn sàng biên tập</p>
              <button
                type="button"
                className={`preview-play ${playing ? 'is-playing' : ''}`}
                aria-label={playing ? 'Tạm dừng' : 'Phát video'}
                onClick={onTogglePlay}
              >
                {playing ? <span className="pause-glyph" /> : <Play size={24} fill="currentColor" />}
              </button>
            </div>
          </div>
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
    </section>
  )
}
