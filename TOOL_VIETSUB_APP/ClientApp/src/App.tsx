import { useCallback, useEffect, useRef, useState } from 'react'
import type {
  CSSProperties,
  KeyboardEvent as ReactKeyboardEvent,
  PointerEvent as ReactPointerEvent,
} from 'react'
import { CheckCircle2, Info, TriangleAlert, X } from 'lucide-react'
import { AccountView } from './components/AccountView'
import { AuthScreen } from './components/AuthScreen'
import { Header } from './components/Header'
import { ImportProgress } from './components/ImportProgress'
import { ImportVideoDialog } from './components/ImportVideoDialog'
import { PreviewPanel } from './components/PreviewPanel'
import { ProjectDialog } from './components/ProjectDialog'
import { SettingsPanel } from './components/SettingsPanel'
import { SubtitlePanel } from './components/SubtitlePanel'
import { Timeline } from './components/Timeline'
import { demoSegments } from './data/mock'
import { hasNativeHost, postToHost, subscribeToHost } from './lib/host'
import { defaultSubtitleStyle } from './lib/subtitleStyle'
import type {
  AuthState,
  MediaImportState,
  ProjectInfo,
  ProjectSummaryInfo,
  RegistrationChallenge,
  RegistrationState,
  SubtitleRemovalSettings,
  SubtitleSegment,
  SubtitleStyleSettings,
  ToastMessage,
  VideoInfo,
} from './types'

const timelineDuration = 21
const demoMode = new URLSearchParams(window.location.search).get('demo') === '1'
const authPreviewMode = new URLSearchParams(window.location.search).get('auth')
const workspacePreviewMode = new URLSearchParams(window.location.search).get('workspace')
const initialView = new URLSearchParams(window.location.search).get('view')
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

type ProjectAudioSettings = Pick<ProjectInfo['settings'],
  | 'originalAudioEnabled'
  | 'originalAudioVolumePercent'
  | 'vietnameseVoiceEnabled'
  | 'vietnameseVoiceVolumePercent'>

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

const demoProject: ProjectInfo = {
  projectId: '1f4a634d-33b6-4caa-8fb8-a48cbeccf2cc',
  name: 'Video giới thiệu sản phẩm Nhật Bản',
  status: 'READY',
  needsRecovery: false,
  serverSynchronized: true,
  updatedAtUtc: '2026-08-09T09:20:00Z',
  sourceLanguageCode: 'zh',
  targetLanguageCode: 'vi',
  settings: {
    speechModel: 'whisper-balanced',
    ocrLanguageCode: 'zh',
    translationModelId: 'opus-mt-zh-vi-official-v2',
    originalAudioEnabled: true,
    originalAudioVolumePercent: 85,
    vietnameseVoiceEnabled: true,
    vietnameseVoiceVolumePercent: 100,
    removeOriginalSubtitles: false,
    originalSubtitleRemovalMode: 'blur',
    originalSubtitleRegionX: 0.05,
    originalSubtitleRegionY: 0.70,
    originalSubtitleRegionWidth: 0.90,
    originalSubtitleRegionHeight: 0.16,
    subtitleStyle: defaultSubtitleStyle,
  },
  video: demoVideo,
  voicePlaybackUrl: null,
  subtitles: demoSegments,
  jobs: [],
}

const demoProjects: ProjectSummaryInfo[] = [
  {
    projectId: demoProject.projectId,
    name: demoProject.name,
    status: 'READY',
    updatedAtUtc: demoProject.updatedAtUtc,
    needsRecovery: false,
    sourceFileName: demoVideo.fileName,
    durationSeconds: demoVideo.durationSeconds,
  },
  {
    projectId: 'af10d202-5df3-4142-bb02-9d1e86b01fb0',
    name: 'Khóa học tiếng Hàn – Bài 03',
    status: 'PROCESSING',
    updatedAtUtc: '2026-08-08T14:10:00Z',
    needsRecovery: true,
    sourceFileName: 'khoa-hoc-bai-03.mkv',
    durationSeconds: 642,
  },
]

const demoAuthState: AuthState = {
  status: 'authenticated',
  account: {
    userId: '70f167a8-49e9-4381-8dc7-215b957938bb',
    email: 'dungdev@toolvietsub.vn',
    displayName: 'Dũng Developer',
    role: 'ADMIN',
    status: 'ACTIVE',
    emailConfirmed: true,
    createdAtUtc: '2026-08-01T02:00:00Z',
    lastLoginAtUtc: '2026-08-09T03:25:00Z',
  },
  entitlements: {
    plan: {
      code: 'PRO',
      displayName: 'Chuyên nghiệp',
      description: 'Đầy đủ công cụ dịch, tạo giọng và xử lý hàng loạt.',
      status: 'ACTIVE',
      startsAtUtc: '2026-08-01T00:00:00Z',
      endsAtUtc: null,
    },
    quota: {
      monthlyMinutes: 1200,
      usedMinutes: 286.5,
      reservedMinutes: 12.5,
      remainingMinutes: 901,
      maxVideoMinutes: 120,
      periodStartsAtUtc: '2026-08-01T00:00:00Z',
      periodEndsAtUtc: '2026-09-01T00:00:00Z',
    },
    features: [
      'subtitle.transcribe', 'subtitle.translate', 'voice.generate',
      'ocr.detect', 'video.export', 'batch.process',
    ],
    evaluatedAtUtc: '2026-08-09T03:25:00Z',
  },
  history: {
    page: 1,
    pageSize: 20,
    totalCount: 3,
    items: [
      { eventId: '1', operationCode: 'MEDIA_PROCESSING', quantity: 12.5, unitCode: 'MINUTE', occurredAtUtc: '2026-08-09T02:15:00Z', projectId: null, jobId: null },
      { eventId: '2', operationCode: 'TRANSCRIPTION', quantity: 12.5, unitCode: 'MINUTE', occurredAtUtc: '2026-08-08T09:30:00Z', projectId: null, jobId: null },
      { eventId: '3', operationCode: 'TTS', quantity: 1840, unitCode: 'CHARACTER', occurredAtUtc: '2026-08-08T09:41:00Z', projectId: null, jobId: null },
    ],
  },
}

const signedOutState: AuthState = {
  status: 'unauthenticated',
  account: null,
  entitlements: null,
  history: null,
}

const emptyRegistrationState: RegistrationState = {
  challenge: null,
  busy: false,
  operation: null,
  errorCode: null,
  errorMessage: null,
}

const emptyImportState: MediaImportState = {
  active: false,
  fileName: null,
  mode: 'COPY',
  percent: 0,
  bytesProcessed: 0,
  totalBytes: 0,
  megabytesPerSecond: 0,
}

const otpPreviewChallenge: RegistrationChallenge = {
  challengeId: '03e775cb-089f-4c91-9d80-dc0578e433ee',
  maskedEmail: 'd***g@gmail.com',
  expiresAtUtc: new Date(Date.now() + 5 * 60_000).toISOString(),
  resendAtUtc: new Date(Date.now() + 60_000).toISOString(),
  resendsRemaining: 3,
}

function App() {
  const [authState, setAuthState] = useState<AuthState>(
    demoMode && !authPreviewMode ? demoAuthState : { ...signedOutState, status: 'loading' },
  )
  const [registrationState, setRegistrationState] = useState<RegistrationState>({
    ...emptyRegistrationState,
    challenge: authPreviewMode === 'otp' ? otpPreviewChallenge : null,
  })
  const [video, setVideo] = useState<VideoInfo | null>(demoMode ? demoVideo : null)
  const [projects, setProjects] = useState<ProjectSummaryInfo[]>(demoMode ? demoProjects : [])
  const [currentProject, setCurrentProject] = useState<ProjectInfo | null>(demoMode ? demoProject : null)
  const [projectDialogOpen, setProjectDialogOpen] = useState(workspacePreviewMode === 'projects')
  const [importDialogOpen, setImportDialogOpen] = useState(workspacePreviewMode === 'import')
  const [projectBusy, setProjectBusy] = useState(false)
  const [jobBusy, setJobBusy] = useState(false)
  const [modelDownloadPercent, setModelDownloadPercent] = useState<number | null>(null)
  const [subtitleBusy, setSubtitleBusy] = useState(false)
  const [projectError, setProjectError] = useState<string | null>(null)
  const [importState, setImportState] = useState<MediaImportState>(emptyImportState)
  const [segments, setSegments] = useState<SubtitleSegment[]>(demoMode ? demoSegments : [])
  const [selectedSegmentId, setSelectedSegmentId] = useState<number | null>(
    demoMode ? demoSegments[0].id : null,
  )
  const [activeNav, setActiveNav] = useState(initialView === 'account' ? 'account' : 'subtitle')
  const [playing, setPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [playbackRate, setPlaybackRate] = useState(1)
  const [maximized, setMaximized] = useState(false)
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const [layoutSizes, setLayoutSizes] = useState<LayoutSizes>(readLayoutSizes)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const editorLayoutRef = useRef<HTMLElement>(null)
  const workspaceGridRef = useRef<HTMLDivElement>(null)
  const toastSequence = useRef(0)
  const subtitleStyleSaveTimer = useRef<number | null>(null)
  const audioSettingsSaveTimer = useRef<number | null>(null)

  useEffect(() => () => {
    if (subtitleStyleSaveTimer.current !== null) {
      window.clearTimeout(subtitleStyleSaveTimer.current)
    }
    if (audioSettingsSaveTimer.current !== null) {
      window.clearTimeout(audioSettingsSaveTimer.current)
    }
  }, [])

  useEffect(() => {
    if (subtitleStyleSaveTimer.current !== null) {
      window.clearTimeout(subtitleStyleSaveTimer.current)
      subtitleStyleSaveTimer.current = null
    }
    if (audioSettingsSaveTimer.current !== null) {
      window.clearTimeout(audioSettingsSaveTimer.current)
      audioSettingsSaveTimer.current = null
    }
  }, [currentProject?.projectId])

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

  const handlePlaybackError = useCallback(() => {
    setPlaying(false)
    notify(
      'Không thể phát video',
      'Codec video chưa được WebView2 hỗ trợ. Pipeline local vẫn có thể xử lý bằng FFmpeg.',
      'warning',
    )
  }, [notify])

  const handleVoicePlaybackError = useCallback(() => {
    setPlaying(false)
    notify(
      'Không thể phát giọng Việt',
      'Track giọng Việt chưa sẵn sàng hoặc file audio đã thay đổi. Hãy tạo giọng lại.',
      'warning',
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

      if (message.type === 'project:list' && Array.isArray(message.projects)) {
        setProjects(message.projects as ProjectSummaryInfo[])
      }

      if (message.type === 'project:busy') {
        setProjectBusy(true)
        setProjectError(null)
      }

      if (
        message.type === 'project:state'
        && typeof message.project === 'object'
        && message.project !== null
      ) {
        const project = message.project as ProjectInfo
        setCurrentProject(project)
        setProjectBusy(false)
        setProjectError(null)
        setProjectDialogOpen(false)
        setSegments(project.subtitles ?? [])
        setSelectedSegmentId(project.subtitles?.[0]?.id ?? null)
        if (project.video) {
          setVideo(project.video)
        } else {
          setVideo(null)
        }
      }

      if (message.type === 'video:import:started') {
        setImportDialogOpen(false)
        setImportState({
          ...emptyImportState,
          active: true,
          fileName: String(message.fileName ?? 'video'),
          mode: message.mode === 'LINK' ? 'LINK' : 'COPY',
        })
      }

      if (
        message.type === 'video:import:progress'
        && typeof message.progress === 'object'
        && message.progress !== null
      ) {
        const progress = message.progress as Record<string, unknown>
        setImportState((current) => ({
          ...current,
          active: true,
          percent: Number(progress.percent ?? 0),
          bytesProcessed: Number(progress.bytesProcessed ?? 0),
          totalBytes: Number(progress.totalBytes ?? 0),
          megabytesPerSecond: Number(progress.megabytesPerSecond ?? 0),
        }))
      }

      if (
        message.type === 'video:import:completed'
        && typeof message.video === 'object'
        && message.video !== null
      ) {
        const importedVideo = message.video as VideoInfo
        setImportState(emptyImportState)
        setVideo(importedVideo)
        setSegments([])
        setSelectedSegmentId(null)
        setCurrentTime(0)
        notify(
          'Video đã sẵn sàng',
          importedVideo.hasAudio === false
            ? 'Đã nhập video nhưng không phát hiện audio. Bạn vẫn có thể dùng OCR phụ đề cứng.'
            : `${importedVideo.fileName} đã được kiểm tra và bảo vệ trong workspace.`,
          importedVideo.hasAudio === false ? 'warning' : 'success',
        )
      }

      if (message.type === 'job:changed' && typeof message.job === 'object' && message.job !== null) {
        const changedJob = message.job as ProjectInfo['jobs'][number]
        setCurrentProject((current) => current ? ({
          ...current,
          jobs: current.jobs.some((job) => job.jobId === changedJob.jobId)
            ? current.jobs.map((job) => job.jobId === changedJob.jobId ? changedJob : job)
            : [...current.jobs, changedJob],
        }) : current)
        setJobBusy(changedJob.status === 'pending' || changedJob.status === 'running')
      }

      if (message.type === 'job:busy') {
        setJobBusy(true)
      }

      if (message.type === 'model:download:progress'
        && typeof message.progress === 'object'
        && message.progress !== null) {
        const progress = message.progress as { percent?: number }
        const percent = Math.max(0, Math.min(100, Number(progress.percent ?? 0)))
        setModelDownloadPercent(percent)
        if (percent >= 100) setModelDownloadPercent(null)
      }

      if (message.type === 'runtime:install:progress'
        && typeof message.progress === 'object'
        && message.progress !== null) {
        const progress = message.progress as { percent?: number }
        const percent = Math.max(0, Math.min(100, Number(progress.percent ?? 0)))
        setModelDownloadPercent(percent)
        if (percent >= 100) setModelDownloadPercent(null)
      }

      if (message.type === 'subtitle:busy') {
        setSubtitleBusy(true)
      }

      if (message.type === 'subtitle:saved') {
        setSubtitleBusy(false)
        const operation = String(message.operation ?? 'update')
        notify(
          operation === 'export' ? 'Đã xuất phụ đề' : 'Đã lưu phụ đề',
          operation === 'export'
            ? 'Tệp SRT tiếng Việt đã được ghi an toàn.'
            : 'Thay đổi đã được lưu vào workspace dự án.',
          'success',
        )
      }

      if (message.type === 'workspace:error') {
        const code = String(message.code ?? 'WORKSPACE_ERROR')
        const errorMessage = String(message.message ?? 'Không thể xử lý workspace.')
        setProjectBusy(false)
        setJobBusy(false)
        setModelDownloadPercent(null)
        setSubtitleBusy(false)
        setImportState(emptyImportState)
        setProjectError(errorMessage)
        if (code === 'PROJECT_REQUIRED' || code.startsWith('PROJECT_')) {
          setProjectDialogOpen(true)
        }
        notify(
          code === 'MEDIA_IMPORT_CANCELLED' ? 'Đã hủy nhập video' : 'Chưa thể thực hiện',
          errorMessage,
          'warning',
        )
      }

      if (message.type === 'window:state') {
        setMaximized(Boolean(message.maximized))
      }

      if (message.type === 'auth:state' && typeof message.state === 'object' && message.state !== null) {
        const nextState = message.state as AuthState
        setAuthState((current) => (
          nextState.status === 'loading' && current.status === 'authenticated'
            ? current
            : nextState
        ))
        if (nextState.status === 'authenticated') {
          setRegistrationState(emptyRegistrationState)
        } else if (nextState.status === 'unauthenticated') {
          setProjects([])
          setCurrentProject(null)
          setVideo(null)
        }
      }

      if (message.type === 'auth:register:busy') {
        const operation = message.operation === 'verify' || message.operation === 'resend'
          ? message.operation
          : 'start'
        setRegistrationState((current) => ({
          ...current,
          busy: true,
          operation,
          errorCode: null,
          errorMessage: null,
        }))
      }

      if (
        message.type === 'auth:register:challenge'
        && typeof message.challenge === 'object'
        && message.challenge !== null
      ) {
        setRegistrationState({
          ...emptyRegistrationState,
          challenge: message.challenge as RegistrationChallenge,
        })
      }

      if (message.type === 'auth:register:error') {
        setRegistrationState((current) => ({
          ...current,
          busy: false,
          operation: null,
          errorCode: String(message.code ?? 'REGISTRATION_FAILED'),
          errorMessage: String(message.message ?? 'Chưa thể xử lý đăng ký.'),
        }))
      }
    })

    postToHost('app:ready')
    if (!hasNativeHost()) {
      setAuthState(demoMode && !authPreviewMode ? demoAuthState : signedOutState)
    }
    return unsubscribe
  }, [loadVideo, notify])

  useEffect(() => {
    if (authState.status === 'authenticated' && hasNativeHost()) {
      postToHost('project:list')
    }
  }, [authState.status])

  useEffect(() => {
    if (!playing || !video || video.playbackUrl) return

    const timer = window.setInterval(() => {
      setCurrentTime((time) => {
        if (time >= video.durationSeconds) {
          setPlaying(false)
          return 0
        }
        return Math.min(video.durationSeconds, time + 0.1)
      })
    }, 100)

    return () => window.clearInterval(timer)
  }, [playing, video])

  useEffect(() => {
    const source = video?.playbackUrl
    if (!source?.startsWith('blob:')) return
    return () => URL.revokeObjectURL(source)
  }, [video?.playbackUrl])

  const openVideo = () => {
    if (!currentProject && hasNativeHost()) {
      setProjectError(null)
      setProjectDialogOpen(true)
      notify('Chọn dự án', 'Hãy tạo hoặc mở dự án trước khi nhập video.', 'info')
      return
    }

    if (hasNativeHost()) {
      setImportDialogOpen(true)
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
      playbackUrl: URL.createObjectURL(file),
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

  const subtitleRemoval: SubtitleRemovalSettings = {
    enabled: currentProject?.settings.removeOriginalSubtitles ?? false,
    mode: currentProject?.settings.originalSubtitleRemovalMode ?? 'blur',
    x: currentProject?.settings.originalSubtitleRegionX ?? 0.05,
    y: currentProject?.settings.originalSubtitleRegionY ?? 0.70,
    width: currentProject?.settings.originalSubtitleRegionWidth ?? 0.90,
    height: currentProject?.settings.originalSubtitleRegionHeight ?? 0.16,
  }

  const updateSubtitleRemoval = (next: SubtitleRemovalSettings) => {
    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        removeOriginalSubtitles: next.enabled,
        originalSubtitleRemovalMode: next.mode,
        originalSubtitleRegionX: next.x,
        originalSubtitleRegionY: next.y,
        originalSubtitleRegionWidth: next.width,
        originalSubtitleRegionHeight: next.height,
      },
    }) : current)
    if (hasNativeHost()) {
      postToHost('project:subtitle-removal:update', next)
    }
  }

  const subtitleStyle: SubtitleStyleSettings = currentProject?.settings.subtitleStyle
    ?? defaultSubtitleStyle

  const updateSubtitleStyle = (next: SubtitleStyleSettings) => {
    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        subtitleStyle: next,
      },
    }) : current)

    if (!hasNativeHost()) return
    if (subtitleStyleSaveTimer.current !== null) {
      window.clearTimeout(subtitleStyleSaveTimer.current)
    }
    subtitleStyleSaveTimer.current = window.setTimeout(() => {
      postToHost('project:subtitle-style:update', next)
      subtitleStyleSaveTimer.current = null
    }, 160)
  }

  const audioSettings: ProjectAudioSettings = {
    originalAudioEnabled: currentProject?.settings.originalAudioEnabled ?? true,
    originalAudioVolumePercent: currentProject?.settings.originalAudioVolumePercent ?? 85,
    vietnameseVoiceEnabled: currentProject?.settings.vietnameseVoiceEnabled ?? true,
    vietnameseVoiceVolumePercent: currentProject?.settings.vietnameseVoiceVolumePercent ?? 100,
  }
  const sourceAudioAvailable = Boolean(video && video.hasAudio !== false)
  const voiceTrackAvailable = segments.some((segment) => segment.hasVoice)
  const voicePreviewAvailable = Boolean(currentProject?.voicePlaybackUrl && voiceTrackAvailable)

  const updateAudioSettings = (next: ProjectAudioSettings, immediate = false) => {
    const normalized: ProjectAudioSettings = {
      ...next,
      originalAudioVolumePercent: Math.min(100, Math.max(0, next.originalAudioVolumePercent)),
      vietnameseVoiceVolumePercent: Math.min(100, Math.max(0, next.vietnameseVoiceVolumePercent)),
    }
    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        ...normalized,
      },
    }) : current)

    if (!hasNativeHost()) return
    if (audioSettingsSaveTimer.current !== null) {
      window.clearTimeout(audioSettingsSaveTimer.current)
    }
    const save = () => {
      postToHost('project:audio-settings:update', normalized)
      audioSettingsSaveTimer.current = null
    }
    if (immediate) {
      save()
      return
    }
    audioSettingsSaveTimer.current = window.setTimeout(save, 160)
  }

  if (authState.status !== 'authenticated' || !authState.account || !authState.entitlements) {
    return (
      <AuthScreen
        authState={authState}
        registrationState={registrationState}
        initialMode={authPreviewMode === 'register' || authPreviewMode === 'otp' ? 'register' : 'login'}
        maximized={maximized}
        onLogin={(email, password) => {
          if (hasNativeHost()) {
            postToHost('auth:login', { email, password })
            return
          }
          setAuthState({
            ...signedOutState,
            errorMessage: 'Màn hình xem trước chưa kết nối với WinForms host.',
          })
        }}
        onRegister={(displayName, email, password) => {
          setRegistrationState((current) => ({
            ...current,
            busy: true,
            operation: 'start',
            errorCode: null,
            errorMessage: null,
          }))
          if (hasNativeHost()) {
            postToHost('auth:register:start', { displayName, email, password })
            return
          }
          window.setTimeout(() => {
            setRegistrationState({ ...emptyRegistrationState, challenge: otpPreviewChallenge })
          }, 500)
        }}
        onVerifyOtp={(challengeId, otp) => {
          setRegistrationState((current) => ({
            ...current,
            busy: true,
            operation: 'verify',
            errorCode: null,
            errorMessage: null,
          }))
          if (hasNativeHost()) postToHost('auth:register:verify', { challengeId, otp })
        }}
        onResendOtp={(challengeId) => {
          setRegistrationState((current) => ({
            ...current,
            busy: true,
            operation: 'resend',
            errorCode: null,
            errorMessage: null,
          }))
          if (hasNativeHost()) postToHost('auth:register:resend', { challengeId })
        }}
        onResetRegistration={() => setRegistrationState(emptyRegistrationState)}
        onRetry={() => postToHost('auth:refresh')}
      />
    )
  }

  return (
    <div className="app-shell">
      <a className="skip-link" href="#editor-workspace">Đi tới vùng biên tập</a>

      <Header
        video={video}
        maximized={maximized}
        activeNav={activeNav}
        account={authState.account}
        currentProject={currentProject}
        onNavChange={setActiveNav}
        onOpenProjects={() => {
          setProjectError(null)
          setProjectDialogOpen(true)
          if (hasNativeHost()) postToHost('project:list')
        }}
        onOpenVideo={openVideo}
        onExportVideo={() => {
          if ((!sourceAudioAvailable || !audioSettings.originalAudioEnabled)
            && (!voiceTrackAvailable || !audioSettings.vietnameseVoiceEnabled)) {
            notify(
              'Chưa có track âm thanh đang bật',
              voiceTrackAvailable
                ? 'Hãy bật Âm gốc hoặc Giọng Việt trước khi xuất video.'
                : 'Hãy tạo giọng Việt hoặc bật lại Âm gốc trước khi xuất video.',
              'warning',
            )
            return
          }
          postToHost('video:export')
        }}
        onNotify={notify}
      />

      {activeNav === 'account' ? (
        <AccountView
          account={authState.account}
          entitlements={authState.entitlements}
          history={authState.history}
          onRefresh={() => postToHost('auth:refresh')}
          onLogout={() => postToHost('auth:logout')}
        />
      ) : (
        <main
          ref={editorLayoutRef}
          id="editor-workspace"
          className="editor-layout"
          style={editorLayoutStyle}
        >
        <div ref={workspaceGridRef} className="workspace-grid" style={workspaceGridStyle}>
          <SettingsPanel
            sourceLanguageCode={currentProject?.sourceLanguageCode ?? 'auto'}
            ocrLanguageCode={currentProject?.settings?.ocrLanguageCode ?? 'auto'}
            translationModelId={currentProject?.settings?.translationModelId ?? 'auto'}
            subtitleRemoval={subtitleRemoval}
            subtitleStyle={subtitleStyle}
            canPrepareAudio={Boolean(video?.hasAudio ?? video)}
            canRunOcr={Boolean(video)}
            audioJob={currentProject?.jobs
              .filter((job) => job.jobType === 'EXTRACT_AUDIO')
              .at(-1) ?? null}
            transcriptionJob={currentProject?.jobs
              .filter((job) => job.jobType === 'TRANSCRIBE_LOCAL')
              .at(-1) ?? null}
            ocrJob={currentProject?.jobs
              .filter((job) => job.jobType === 'OCR_LOCAL')
              .at(-1) ?? null}
            pipelineJob={currentProject?.jobs.at(-1) ?? null}
            modelDownloadPercent={modelDownloadPercent}
            jobBusy={jobBusy}
            onLanguageSettingsChange={(sourceLanguageCode, ocrLanguageCode) => {
              postToHost('project:settings:update', { sourceLanguageCode, ocrLanguageCode })
            }}
            onSubtitleRemovalChange={updateSubtitleRemoval}
            onSubtitleStyleChange={updateSubtitleStyle}
            onPrepareAudio={() => {
              setJobBusy(true)
              postToHost('job:audio:prepare')
            }}
            onTranscribe={() => {
              setJobBusy(true)
              postToHost('job:transcribe')
            }}
            onOcr={() => {
              setJobBusy(true)
              postToHost('job:ocr')
            }}
            onPauseJob={(jobId) => {
              setJobBusy(true)
              postToHost('job:pause', { jobId })
            }}
            onResumeJob={(jobId) => {
              setJobBusy(true)
              postToHost('job:resume', { jobId })
            }}
            onRetryJob={(jobId) => {
              setJobBusy(true)
              postToHost('job:retry', { jobId })
            }}
            onCancelJob={(jobId) => {
              setJobBusy(true)
              postToHost('job:cancel', { jobId })
            }}
          />
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
            currentTime={currentTime}
            playbackRate={playbackRate}
            originalAudioEnabled={audioSettings.originalAudioEnabled && sourceAudioAvailable}
            sourceVolume={audioSettings.originalAudioVolumePercent}
            voiceAudioEnabled={audioSettings.vietnameseVoiceEnabled && voicePreviewAvailable}
            voiceVolume={audioSettings.vietnameseVoiceVolumePercent}
            voicePlaybackUrl={currentProject?.voicePlaybackUrl ?? null}
            subtitleText={segments.find((segment) => (
              currentTime >= segment.start && currentTime < segment.end
            ))?.translated ?? ''}
            subtitleRemoval={subtitleRemoval}
            subtitleStyle={subtitleStyle}
            onTogglePlay={togglePlayback}
            onTimeUpdate={setCurrentTime}
            onPlaybackEnded={() => setPlaying(false)}
            onPlaybackError={handlePlaybackError}
            onVoicePlaybackError={handleVoicePlaybackError}
            onOpenVideo={openVideo}
            onDropVideo={loadBrowserFile}
            onSubtitleRemovalChange={updateSubtitleRemoval}
            onSubtitleStyleChange={updateSubtitleStyle}
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
            busy={subtitleBusy || jobBusy}
            onImportSrt={() => {
              setSubtitleBusy(true)
              postToHost('subtitle:import:srt')
            }}
            onExportSrt={() => {
              setSubtitleBusy(true)
              postToHost('subtitle:export:srt')
            }}
            onTranslate={() => {
              setJobBusy(true)
              postToHost('job:translate')
            }}
            onSynthesizeVoice={() => {
              setJobBusy(true)
              postToHost('job:voice:synthesize')
            }}
            onUpdateSegment={(cueId, original, translated) => {
              setSubtitleBusy(true)
              postToHost('subtitle:update', { cueId, original, translated })
            }}
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
          playbackRate={playbackRate}
          sourceAudioEnabled={audioSettings.originalAudioEnabled}
          sourceVolume={audioSettings.originalAudioVolumePercent}
          sourceAudioAvailable={sourceAudioAvailable}
          voiceAudioEnabled={audioSettings.vietnameseVoiceEnabled}
          voiceVolume={audioSettings.vietnameseVoiceVolumePercent}
          voiceAudioAvailable={voiceTrackAvailable}
          selectedId={selectedSegmentId}
          busy={subtitleBusy || jobBusy}
          onTogglePlay={togglePlayback}
          onSeek={setCurrentTime}
          onPlaybackRateChange={setPlaybackRate}
          onToggleSourceAudio={() => updateAudioSettings({
            ...audioSettings,
            originalAudioEnabled: !audioSettings.originalAudioEnabled,
          }, true)}
          onSourceVolumeChange={(volume) => updateAudioSettings({
            ...audioSettings,
            originalAudioVolumePercent: volume,
          })}
          onToggleVoiceAudio={() => updateAudioSettings({
            ...audioSettings,
            vietnameseVoiceEnabled: !audioSettings.vietnameseVoiceEnabled,
          }, true)}
          onVoiceVolumeChange={(volume) => updateAudioSettings({
            ...audioSettings,
            vietnameseVoiceVolumePercent: volume,
          })}
          onSelectSegment={setSelectedSegmentId}
          onSplitCue={(id, positionSeconds) => {
            const segment = segments.find((item) => item.id === id)
            if (!segment) return
            setSubtitleBusy(true)
            postToHost('timeline:split', { cueId: segment.cueId, positionSeconds })
          }}
          onAlignCue={(id, positionSeconds) => {
            const segment = segments.find((item) => item.id === id)
            if (!segment) return
            setSubtitleBusy(true)
            postToHost('timeline:align', { cueId: segment.cueId, positionSeconds })
          }}
          onDuplicateCue={(id) => {
            const segment = segments.find((item) => item.id === id)
            if (!segment) return
            setSubtitleBusy(true)
            postToHost('timeline:duplicate', { cueId: segment.cueId })
          }}
          onDeleteCue={(id) => {
            const segment = segments.find((item) => item.id === id)
            if (!segment) return
            setSelectedSegmentId(null)
            setSubtitleBusy(true)
            postToHost('timeline:delete', { cueId: segment.cueId })
          }}
          onNotify={notify}
        />
        </main>
      )}

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

      <ProjectDialog
        open={projectDialogOpen}
        projects={projects}
        currentProject={currentProject}
        busy={projectBusy}
        error={projectError}
        onClose={() => {
          if (!projectBusy) setProjectDialogOpen(false)
        }}
        onCreate={(name) => {
          setProjectBusy(true)
          setProjectError(null)
          if (hasNativeHost()) postToHost('project:create', { name })
        }}
        onOpen={(projectId) => {
          setProjectBusy(true)
          setProjectError(null)
          if (hasNativeHost()) postToHost('project:open', { projectId })
        }}
        onRename={(name) => {
          setProjectBusy(true)
          setProjectError(null)
          if (hasNativeHost()) postToHost('project:rename', { name })
        }}
      />

      <ImportVideoDialog
        open={importDialogOpen}
        onClose={() => setImportDialogOpen(false)}
        onContinue={(mode) => {
          setImportDialogOpen(false)
          postToHost('video:open', { mode })
        }}
      />

      <ImportProgress
        state={importState}
        onCancel={() => postToHost('video:import:cancel')}
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
