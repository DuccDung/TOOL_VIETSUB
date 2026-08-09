import { useCallback, useEffect, useRef, useState } from 'react'
import type {
  CSSProperties,
  KeyboardEvent as ReactKeyboardEvent,
  PointerEvent as ReactPointerEvent,
} from 'react'
import { CheckCircle2, Info, TriangleAlert, X } from 'lucide-react'
import { Header } from './components/Header'
import { PreviewPanel } from './components/PreviewPanel'
import { SettingsPanel } from './components/SettingsPanel'
import { SubtitlePanel } from './components/SubtitlePanel'
import { Timeline } from './components/Timeline'
import { demoSegments } from './data/mock'
import { hasNativeHost, postToHost, subscribeToHost } from './lib/host'
import type { SubtitleSegment, ToastMessage, VideoInfo } from './types'

const timelineDuration = 21
const demoMode = new URLSearchParams(window.location.search).get('demo') === '1'
const layoutStorageKey = 'tool-vietsub:editor-layout:v1'
const resizerSize = 10
const editorVerticalPadding = 20
const minSettingsWidth = 250
const maxSettingsWidth = 520
const minPreviewWidth = 400
const minSubtitleWidth = 280
const maxSubtitleWidth = 560
const minWorkspaceHeight = 290
const minTimelineHeight = 240

type ResizeTarget = 'settings' | 'subtitles' | 'timeline'

interface LayoutSizes {
  settingsWidth: number
  subtitleWidth: number
  timelineHeight: number
}

interface LayoutSeparatorProps {
  orientation: 'horizontal' | 'vertical'
  label: string
  value: number
  min: number
  max: number
  onPointerDown: (event: ReactPointerEvent<HTMLDivElement>) => void
  onKeyDown: (event: ReactKeyboardEvent<HTMLDivElement>) => void
}

const defaultLayoutSizes: LayoutSizes = {
  settingsWidth: 315,
  subtitleWidth: 340,
  timelineHeight: 332,
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max)
}

function readLayoutSizes(): LayoutSizes {
  try {
    const saved = JSON.parse(window.localStorage.getItem(layoutStorageKey) ?? '{}') as Partial<LayoutSizes>

    return {
      settingsWidth: Number.isFinite(saved.settingsWidth)
        ? clamp(Number(saved.settingsWidth), minSettingsWidth, maxSettingsWidth)
        : defaultLayoutSizes.settingsWidth,
      subtitleWidth: Number.isFinite(saved.subtitleWidth)
        ? clamp(Number(saved.subtitleWidth), minSubtitleWidth, maxSubtitleWidth)
        : defaultLayoutSizes.subtitleWidth,
      timelineHeight: Number.isFinite(saved.timelineHeight)
        ? Math.max(Number(saved.timelineHeight), minTimelineHeight)
        : defaultLayoutSizes.timelineHeight,
    }
  } catch {
    return defaultLayoutSizes
  }
}

function LayoutSeparator({
  orientation,
  label,
  value,
  min,
  max,
  onPointerDown,
  onKeyDown,
}: LayoutSeparatorProps) {
  return (
    <div
      className={`layout-resizer layout-resizer--${orientation}`}
      role="separator"
      aria-label={label}
      aria-orientation={orientation}
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuenow={Math.round(value)}
      tabIndex={0}
      title={`${label}. Kéo chuột hoặc dùng phím mũi tên.`}
      onPointerDown={onPointerDown}
      onKeyDown={onKeyDown}
    />
  )
}

const demoVideo: VideoInfo = {
  fileName: 'video-gioi-thieu-san-pham.mp4',
  extension: 'MP4',
  sizeBytes: 128 * 1024 * 1024,
  durationSeconds: timelineDuration,
}

function App() {
  const [video, setVideo] = useState<VideoInfo | null>(demoMode ? demoVideo : null)
  const [segments, setSegments] = useState<SubtitleSegment[]>(demoMode ? demoSegments : [])
  const [selectedSegmentId, setSelectedSegmentId] = useState<number | null>(
    demoMode ? demoSegments[0].id : null,
  )
  const [activeNav, setActiveNav] = useState('subtitle')
  const [playing, setPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [maximized, setMaximized] = useState(false)
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const [layoutSizes, setLayoutSizes] = useState<LayoutSizes>(readLayoutSizes)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const editorLayoutRef = useRef<HTMLElement>(null)
  const workspaceGridRef = useRef<HTMLDivElement>(null)
  const toastSequence = useRef(0)

  const fitLayout = useCallback((candidate: LayoutSizes, preferred?: ResizeTarget): LayoutSizes => {
    const workspaceWidth = workspaceGridRef.current?.clientWidth ?? Math.max(window.innerWidth - 20, 1160)
    const sidePanelBudget = Math.max(
      minSettingsWidth + minSubtitleWidth,
      workspaceWidth - (resizerSize * 2) - minPreviewWidth,
    )

    let settingsWidth = clamp(
      Math.round(candidate.settingsWidth),
      minSettingsWidth,
      Math.min(maxSettingsWidth, sidePanelBudget - minSubtitleWidth),
    )
    let subtitleWidth = clamp(
      Math.round(candidate.subtitleWidth),
      minSubtitleWidth,
      Math.min(maxSubtitleWidth, sidePanelBudget - minSettingsWidth),
    )

    if (settingsWidth + subtitleWidth > sidePanelBudget) {
      if (preferred === 'settings') {
        settingsWidth = Math.max(minSettingsWidth, sidePanelBudget - subtitleWidth)
      } else if (preferred === 'subtitles') {
        subtitleWidth = Math.max(minSubtitleWidth, sidePanelBudget - settingsWidth)
      } else {
        let overflow = settingsWidth + subtitleWidth - sidePanelBudget
        const settingsReduction = Math.min(
          Math.ceil(overflow / 2),
          settingsWidth - minSettingsWidth,
        )
        settingsWidth -= settingsReduction
        overflow -= settingsReduction
        subtitleWidth -= Math.min(overflow, subtitleWidth - minSubtitleWidth)
      }
    }

    const editorHeight = editorLayoutRef.current?.clientHeight ?? Math.max(window.innerHeight - 96, 624)
    const usableEditorHeight = Math.max(
      minWorkspaceHeight + minTimelineHeight,
      editorHeight - editorVerticalPadding - resizerSize,
    )
    const maxTimelineHeight = Math.max(
      minTimelineHeight,
      usableEditorHeight - minWorkspaceHeight,
    )

    return {
      settingsWidth,
      subtitleWidth,
      timelineHeight: clamp(
        Math.round(candidate.timelineHeight),
        minTimelineHeight,
        maxTimelineHeight,
      ),
    }
  }, [])

  useEffect(() => {
    const fitToViewport = () => {
      setLayoutSizes((current) => {
        const fitted = fitLayout(current)
        return fitted.settingsWidth === current.settingsWidth
          && fitted.subtitleWidth === current.subtitleWidth
          && fitted.timelineHeight === current.timelineHeight
          ? current
          : fitted
      })
    }

    fitToViewport()
    const observer = new ResizeObserver(fitToViewport)
    if (editorLayoutRef.current) observer.observe(editorLayoutRef.current)
    if (workspaceGridRef.current) observer.observe(workspaceGridRef.current)

    return () => observer.disconnect()
  }, [fitLayout])

  useEffect(() => {
    const saveTimer = window.setTimeout(() => {
      try {
        window.localStorage.setItem(layoutStorageKey, JSON.stringify(layoutSizes))
      } catch {
        // The layout remains usable even when local storage is unavailable.
      }
    }, 160)

    return () => window.clearTimeout(saveTimer)
  }, [layoutSizes])

  const beginResize = useCallback((target: ResizeTarget, event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return

    event.preventDefault()
    const startX = event.clientX
    const startY = event.clientY
    const startLayout = layoutSizes
    const resizingClass = target === 'timeline' ? 'is-resizing-rows' : 'is-resizing-columns'
    document.body.classList.add(resizingClass)

    const handlePointerMove = (pointerEvent: PointerEvent) => {
      const candidate = { ...startLayout }

      if (target === 'settings') {
        candidate.settingsWidth += pointerEvent.clientX - startX
      } else if (target === 'subtitles') {
        candidate.subtitleWidth -= pointerEvent.clientX - startX
      } else {
        candidate.timelineHeight -= pointerEvent.clientY - startY
      }

      setLayoutSizes(fitLayout(candidate, target))
    }

    const finishResize = () => {
      document.body.classList.remove(resizingClass)
      window.removeEventListener('pointermove', handlePointerMove)
      window.removeEventListener('pointerup', finishResize)
      window.removeEventListener('pointercancel', finishResize)
    }

    window.addEventListener('pointermove', handlePointerMove)
    window.addEventListener('pointerup', finishResize)
    window.addEventListener('pointercancel', finishResize)
  }, [fitLayout, layoutSizes])

  const resizeWithKeyboard = useCallback((
    target: ResizeTarget,
    event: ReactKeyboardEvent<HTMLDivElement>,
  ) => {
    const step = event.shiftKey ? 32 : 16
    let delta = 0

    if (target === 'settings') {
      if (event.key === 'ArrowLeft') delta = -step
      if (event.key === 'ArrowRight') delta = step
    } else if (target === 'subtitles') {
      if (event.key === 'ArrowLeft') delta = step
      if (event.key === 'ArrowRight') delta = -step
    } else {
      if (event.key === 'ArrowUp') delta = step
      if (event.key === 'ArrowDown') delta = -step
    }

    if (delta === 0) return
    event.preventDefault()

    setLayoutSizes((current) => {
      const candidate = { ...current }
      if (target === 'settings') candidate.settingsWidth += delta
      if (target === 'subtitles') candidate.subtitleWidth += delta
      if (target === 'timeline') candidate.timelineHeight += delta
      return fitLayout(candidate, target)
    })
  }, [fitLayout])

  const editorLayoutStyle = {
    '--timeline-height': `${layoutSizes.timelineHeight}px`,
  } as CSSProperties
  const workspaceGridStyle = {
    '--settings-panel-width': `${layoutSizes.settingsWidth}px`,
    '--subtitle-panel-width': `${layoutSizes.subtitleWidth}px`,
  } as CSSProperties

  const notify = useCallback((title: string, description: string, tone: ToastMessage['tone'] = 'info') => {
    toastSequence.current += 1
    const id = toastSequence.current
    setToasts((current) => [...current, { id, title, description, tone }])
    window.setTimeout(() => {
      setToasts((current) => current.filter((toast) => toast.id !== id))
    }, 4200)
  }, [])

  const loadVideo = useCallback((nextVideo: VideoInfo) => {
    setVideo(nextVideo)
    setSegments(demoSegments)
    setSelectedSegmentId(demoSegments[0].id)
    setCurrentTime(0)
    setPlaying(false)
    notify(
      'Đã nhập video',
      `${nextVideo.fileName} đã sẵn sàng trong trình biên tập UI.`,
      'success',
    )
  }, [notify])

  useEffect(() => {
    const unsubscribe = subscribeToHost((message) => {
      if (message.type === 'video:selected') {
        loadVideo({
          fileName: String(message.fileName ?? 'video.mp4'),
          extension: String(message.extension ?? 'MP4'),
          sizeBytes: Number(message.sizeBytes ?? 0),
          durationSeconds: timelineDuration,
        })
      }

      if (message.type === 'window:state') {
        setMaximized(Boolean(message.maximized))
      }
    })

    postToHost('app:ready')
    return unsubscribe
  }, [loadVideo])

  useEffect(() => {
    if (!playing || !video) return

    const timer = window.setInterval(() => {
      setCurrentTime((time) => {
        if (time >= timelineDuration) {
          setPlaying(false)
          return 0
        }
        return Math.min(timelineDuration, time + 0.1)
      })
    }, 100)

    return () => window.clearInterval(timer)
  }, [playing, video])

  const openVideo = () => {
    if (hasNativeHost()) {
      postToHost('video:open')
      return
    }

    fileInputRef.current?.click()
  }

  const loadBrowserFile = (file: File) => {
    const extension = file.name.split('.').pop()?.toUpperCase() ?? 'VIDEO'
    loadVideo({
      fileName: file.name,
      extension,
      sizeBytes: file.size,
      durationSeconds: timelineDuration,
    })
  }

  const togglePlayback = () => {
    if (!video) {
      notify('Chưa có video', 'Hãy nhập video trước khi sử dụng điều khiển phát.', 'warning')
      return
    }
    setPlaying((value) => !value)
  }

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null
      const isTyping = target?.matches('input, textarea, select, [contenteditable="true"]')

      if (event.ctrlKey && event.key.toLocaleLowerCase() === 'o') {
        event.preventDefault()
        openVideo()
      }

      if (!isTyping && event.code === 'Space') {
        event.preventDefault()
        togglePlayback()
      }

      if (!isTyping && event.key === 'Escape' && playing) {
        setPlaying(false)
      }
    }

    window.addEventListener('keydown', handleShortcut)
    return () => window.removeEventListener('keydown', handleShortcut)
  })

  return (
    <div className="app-shell">
      <a className="skip-link" href="#editor-workspace">Đi tới vùng biên tập</a>

      <Header
        video={video}
        maximized={maximized}
        activeNav={activeNav}
        onNavChange={setActiveNav}
        onOpenVideo={openVideo}
        onNotify={notify}
      />

      <main
        ref={editorLayoutRef}
        id="editor-workspace"
        className="editor-layout"
        style={editorLayoutStyle}
      >
        <div ref={workspaceGridRef} className="workspace-grid" style={workspaceGridStyle}>
          <SettingsPanel />
          <LayoutSeparator
            orientation="vertical"
            label="Điều chỉnh chiều rộng bảng thiết lập"
            value={layoutSizes.settingsWidth}
            min={minSettingsWidth}
            max={maxSettingsWidth}
            onPointerDown={(event) => beginResize('settings', event)}
            onKeyDown={(event) => resizeWithKeyboard('settings', event)}
          />
          <PreviewPanel
            video={video}
            playing={playing}
            onTogglePlay={togglePlayback}
            onOpenVideo={openVideo}
            onDropVideo={loadBrowserFile}
          />
          <LayoutSeparator
            orientation="vertical"
            label="Điều chỉnh chiều rộng danh sách phụ đề"
            value={layoutSizes.subtitleWidth}
            min={minSubtitleWidth}
            max={maxSubtitleWidth}
            onPointerDown={(event) => beginResize('subtitles', event)}
            onKeyDown={(event) => resizeWithKeyboard('subtitles', event)}
          />
          <SubtitlePanel
            segments={segments}
            selectedId={selectedSegmentId}
            onSelect={(id) => {
              setSelectedSegmentId(id)
              const segment = segments.find((item) => item.id === id)
              if (segment) setCurrentTime(segment.start)
            }}
            onNotify={notify}
          />
        </div>

        <LayoutSeparator
          orientation="horizontal"
          label="Điều chỉnh chiều cao timeline"
          value={layoutSizes.timelineHeight}
          min={minTimelineHeight}
          max={Math.max(minTimelineHeight, window.innerHeight - minWorkspaceHeight)}
          onPointerDown={(event) => beginResize('timeline', event)}
          onKeyDown={(event) => resizeWithKeyboard('timeline', event)}
        />

        <Timeline
          video={video}
          segments={segments}
          playing={playing}
          currentTime={currentTime}
          onTogglePlay={togglePlayback}
          onSeek={setCurrentTime}
          onNotify={notify}
        />
      </main>

      <input
        ref={fileInputRef}
        className="visually-hidden"
        type="file"
        accept="video/mp4,video/x-matroska,video/quicktime,video/webm"
        tabIndex={-1}
        onChange={(event) => {
          const file = event.target.files?.[0]
          if (file) loadBrowserFile(file)
          event.target.value = ''
        }}
      />

      <div className="toast-region" aria-live="polite" aria-atomic="false">
        {toasts.map((toast) => (
          <div key={toast.id} className={`toast toast--${toast.tone ?? 'info'}`}>
            <span className="toast__icon">
              {toast.tone === 'success' ? <CheckCircle2 size={18} /> : null}
              {toast.tone === 'warning' ? <TriangleAlert size={18} /> : null}
              {!toast.tone || toast.tone === 'info' ? <Info size={18} /> : null}
            </span>
            <span className="toast__copy">
              <strong>{toast.title}</strong>
              <span>{toast.description}</span>
            </span>
            <button
              type="button"
              aria-label="Đóng thông báo"
              onClick={() => setToasts((current) => current.filter((item) => item.id !== toast.id))}
            >
              <X size={15} />
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}

export default App
