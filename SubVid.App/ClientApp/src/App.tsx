import { useCallback, useEffect, useRef, useState } from 'react'
import type {
  CSSProperties,
  KeyboardEvent as ReactKeyboardEvent,
  PointerEvent as ReactPointerEvent,
} from 'react'
import { CheckCircle2, Info, TriangleAlert, X } from 'lucide-react'
import { AccountView } from './components/AccountView'
import { AiStorageDialog, type AiStorageSelection } from './components/AiStorageDialog'
import { ApiErrorDialog } from './components/ApiErrorDialog'
import { AuthScreen } from './components/AuthScreen'
import { FfmpegSetupDialog } from './components/FfmpegSetupDialog'
import { Header } from './components/Header'
import { ImportProgress } from './components/ImportProgress'
import { ImportVideoDialog } from './components/ImportVideoDialog'
import { PreviewPanel } from './components/PreviewPanel'
import { ProjectDialog } from './components/ProjectDialog'
import { SettingsPanel } from './components/SettingsPanel'
import { SubtitlePanel } from './components/SubtitlePanel'
import { Timeline } from './components/Timeline'
import { TranslationRetryDialog } from './components/TranslationRetryDialog'
import { VoiceSelectionDialog } from './components/VoiceSelectionDialog'
import { VoiceWorkspace } from './components/VoiceWorkspace'
import { demoSegments } from './data/mock'
import { hasNativeHost, postToHost, subscribeToHost } from './lib/host'
import { defaultSubtitleStyle } from './lib/subtitleStyle'
import type {
  AuthState,
  FfmpegInstallProgress,
  FfmpegRuntimeStatus,
  MediaImportState,
  LocalJobInfo,
  ProjectInfo,
  ProjectSummaryInfo,
  RegistrationChallenge,
  RegistrationState,
  SubtitleRemovalSettings,
  SubtitleSegment,
  SubtitleStyleSettings,
  ToastMessage,
  TranslationSettingsInfo,
  VideoInfo,
  VideoTransformSettings,
  VoiceSettingsInfo,
} from './types'

const timelineDuration = 21
const defaultTranslationSettings: TranslationSettingsInfo = {
  provider: 'local',
  modelId: 'auto',
  qualityMode: 'balanced',
  reviewEnabled: true,
  fallbackToLocal: false,
  apiKeyConfigured: false,
  projectContext: '',
  characterInstructions: '',
  styleInstructions: 'Tiếng Việt tự nhiên, rõ nghĩa, phù hợp lời thoại và không tự ý thêm thông tin.',
  glossaryText: '',
  translationMemoryCount: 0,
}
const defaultVoiceSettings: VoiceSettingsInfo = {
  defaultVoiceId: 'piper:vi-vn-vais1000',
  speakerVoiceIds: {},
  speed: 0,
  fptApiKeyConfigured: false,
  estimatedCharacters: 0,
  voices: [
    { voiceId: 'piper:vi-vn-vais1000', engine: 'piper', displayName: 'VAIS-1000', gender: 'Nữ', region: 'Việt Nam', style: 'Tự nhiên', modelVersion: 'medium', license: 'MIT', installed: false, isCloud: false, requiresInstall: true },
    { voiceId: 'vieneu:minh-duc', engine: 'vieneu', displayName: 'Minh Đức', gender: 'Nam', region: 'Bắc', style: 'Tin tức', modelVersion: '3.2.5', license: 'Apache-2.0', installed: false, isCloud: false, requiresInstall: true },
    { voiceId: 'vieneu:truc-ly', engine: 'vieneu', displayName: 'Trúc Ly', gender: 'Nữ', region: 'Bắc', style: 'Tự nhiên', modelVersion: '3.2.5', license: 'Apache-2.0', installed: false, isCloud: false, requiresInstall: true },
    { voiceId: 'fpt:banmai', engine: 'fpt', displayName: 'Ban Mai', gender: 'Nữ', region: 'Bắc', style: 'Tự nhiên', modelVersion: 'v5', license: 'FPT.AI Terms', installed: false, isCloud: true, requiresInstall: false },
    { voiceId: 'fpt:leminh', engine: 'fpt', displayName: 'Lê Minh', gender: 'Nam', region: 'Bắc', style: 'Tự nhiên', modelVersion: 'v5', license: 'FPT.AI Terms', installed: false, isCloud: true, requiresInstall: false },
  ],
}
const demoMode = new URLSearchParams(window.location.search).get('demo') === '1'
const authPreviewMode = new URLSearchParams(window.location.search).get('auth')
const workspacePreviewMode = new URLSearchParams(window.location.search).get('workspace')
const initialView = new URLSearchParams(window.location.search).get('view')
const layoutStorageKey = 'subvid:editor-layout:v1'
const resizerSize = 10
const editorVerticalPadding = 20
const minSettingsWidth = 250
const maxSettingsWidth = 520
const minPreviewWidth = 400
const minSubtitleWidth = 280
const maxSubtitleWidth = 560
const minWorkspaceHeight = 290
const minTimelineHeight = 240

function mirrorRegionCoordinate(position: number, size: number) {
  return Math.min(Math.max(1 - position - size, 0), Math.max(0, 1 - size))
}

type ResizeTarget = 'settings' | 'subtitles' | 'timeline'

type ApiErrorState = {
  provider: string
  code: string
  message: string
}

const cloudTranslationApiErrorCodes = new Set([
  'TRANSLATION_API_KEY_REQUIRED',
  'TRANSLATION_API_KEY_INVALID',
  'TRANSLATION_API_ACCESS_DENIED',
  'TRANSLATION_BALANCE_EXHAUSTED',
  'TRANSLATION_RATE_LIMITED',
  'TRANSLATION_REQUEST_REJECTED',
  'TRANSLATION_MODEL_UNAVAILABLE',
  'TRANSLATION_REQUEST_TOO_LARGE',
  'TRANSLATION_REQUEST_CANCELLED',
  'TRANSLATION_PROVIDER_ERROR',
  'TRANSLATION_PROVIDER_TIMEOUT',
  'TRANSLATION_PROVIDER_UNAVAILABLE',
  'TRANSLATION_NETWORK_ERROR',
  'TRANSLATION_RESPONSE_TOO_LARGE',
  'TRANSLATION_RESPONSE_INCOMPLETE',
  'TRANSLATION_RESPONSE_INVALID',
  'TRANSLATION_RESULT_INVALID',
  'TRANSLATION_REFUSED',
])

function isCloudTranslationApiError(code: string) {
  return cloudTranslationApiErrorCodes.has(code)
}

function isCloudVoiceApiError(code: string) {
  return code.startsWith('FPT_')
}

function inferTranslationProvider(message: string) {
  const normalizedMessage = message.toLowerCase()
  if (normalizedMessage.includes('groq')) return 'Groq'
  if (normalizedMessage.includes('deepseek')) return 'DeepSeek'
  if (normalizedMessage.includes('gemini')) return 'Gemini'
  if (normalizedMessage.includes('openai')) return 'OpenAI'
  return 'dịch vụ AI cloud'
}

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
    translation: {
      ...defaultTranslationSettings,
      modelId: 'opus-mt-zh-vi-official-v2',
    },
    voice: defaultVoiceSettings,
    originalAudioEnabled: true,
    originalAudioVolumePercent: 85,
    vietnameseVoiceEnabled: true,
    vietnameseVoiceVolumePercent: 100,
    vietnameseSubtitlesEnabled: true,
    flipHorizontal: false,
    flipVertical: false,
    removeOriginalSubtitles: false,
    originalSubtitleRemovalMode: 'blur',
    originalSubtitleRegionX: 0.05,
    originalSubtitleRegionY: 0.70,
    originalSubtitleRegionWidth: 0.90,
    originalSubtitleRegionHeight: 0.16,
    subtitleStyle: defaultSubtitleStyle,
  },
  aiStorage: {
    rootPath: 'D:\\SUBVID_AI',
    freeBytes: 47 * 1024 * 1024 * 1024,
    usesLegacyLocation: false,
    recommendedPath: 'D:\\SUBVID_AI',
    pendingMigrationPath: null,
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
    email: 'dungdev@subvid.vn',
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

const emptyFfmpegStatus: FfmpegRuntimeStatus = {
  state: 'MISSING',
  ready: false,
  managed: false,
  source: 'NONE',
  version: null,
  targetVersion: '9.0.1',
  ffmpegPath: null,
  ffprobePath: null,
  installDirectory: '',
  downloadBytes: 111253802,
  license: 'GPL-3.0',
  sourceUrl: 'https://github.com/FFmpeg/FFmpeg',
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
  const [ffmpegStatus, setFfmpegStatus] = useState<FfmpegRuntimeStatus>(emptyFfmpegStatus)
  const [ffmpegProgress, setFfmpegProgress] = useState<FfmpegInstallProgress | null>(null)
  const [ffmpegDialogOpen, setFfmpegDialogOpen] = useState(false)
  const [ffmpegError, setFfmpegError] = useState<string | null>(null)
  const [ffmpegPendingFileName, setFfmpegPendingFileName] = useState<string | null>(null)
  const [ffmpegForce, setFfmpegForce] = useState(false)
  const [segments, setSegments] = useState<SubtitleSegment[]>(demoMode ? demoSegments : [])
  const [selectedSegmentId, setSelectedSegmentId] = useState<number | null>(
    demoMode ? demoSegments[0].id : null,
  )
  const [activeNav, setActiveNav] = useState(
    ['account', 'voice', 'library'].includes(initialView ?? '') ? initialView! : 'subtitle',
  )
  const [playing, setPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [playbackRate, setPlaybackRate] = useState(1)
  const [maximized, setMaximized] = useState(false)
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const [apiError, setApiError] = useState<ApiErrorState | null>(null)
  const [translationRetryJob, setTranslationRetryJob] = useState<LocalJobInfo | null>(null)
  const [voiceSelectionOpen, setVoiceSelectionOpen] = useState(false)
  const [voicePreviewBusy, setVoicePreviewBusy] = useState(false)
  const [voicePreviewDataUrl, setVoicePreviewDataUrl] = useState<string | null>(null)
  const [aiStorageSelection, setAiStorageSelection] = useState<AiStorageSelection | null>(null)
  const [aiStorageProgress, setAiStorageProgress] = useState<{ percent: number, message: string } | null>(null)
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
        setSelectedSegmentId((current) => (
          project.subtitles?.some((segment) => segment.id === current)
            ? current
            : project.subtitles?.[0]?.id ?? null
        ))
        if (project.video) {
          setVideo(project.video)
        } else {
          setVideo(null)
        }
      }

      if (message.type === 'ffmpeg:status'
        && typeof message.status === 'object'
        && message.status !== null) {
        const status = message.status as FfmpegRuntimeStatus
        setFfmpegStatus(status)
        if (status.ready) {
          setFfmpegDialogOpen(false)
          setFfmpegError(null)
          setFfmpegPendingFileName(null)
        }
      }

      if (message.type === 'ffmpeg:install:required'
        && typeof message.status === 'object'
        && message.status !== null) {
        setFfmpegStatus(message.status as FfmpegRuntimeStatus)
        setFfmpegPendingFileName(String(message.fileName ?? 'video'))
        setFfmpegProgress(null)
        setFfmpegError(null)
        setFfmpegForce(false)
        setFfmpegDialogOpen(true)
      }

      if (message.type === 'ffmpeg:install:progress'
        && typeof message.progress === 'object'
        && message.progress !== null) {
        const progress = message.progress as FfmpegInstallProgress
        if (progress.phase === 'READY') {
          setFfmpegProgress(null)
          return
        }
        setFfmpegProgress(progress)
        setFfmpegError(null)
        setFfmpegStatus((current) => ({ ...current, state: 'INSTALLING' }))
      }

      if (message.type === 'ffmpeg:install:completed'
        && typeof message.status === 'object'
        && message.status !== null) {
        const status = message.status as FfmpegRuntimeStatus
        setFfmpegStatus(status)
        setFfmpegProgress(null)
        setFfmpegError(null)
        setFfmpegDialogOpen(false)
        setFfmpegPendingFileName(null)
        notify('Công cụ video đã sẵn sàng', `FFmpeg ${status.version ?? status.targetVersion} đã được cài và xác minh.`, 'success')
      }

      if (message.type === 'ffmpeg:install:failed') {
        if (typeof message.status === 'object' && message.status !== null) {
          setFfmpegStatus({ ...(message.status as FfmpegRuntimeStatus), state: 'ERROR' })
        } else {
          setFfmpegStatus((current) => ({ ...current, state: 'ERROR' }))
        }
        setFfmpegProgress(null)
        setFfmpegError(String(message.message ?? 'Không thể cài FFmpeg.'))
        setFfmpegDialogOpen(true)
      }

      if (message.type === 'ffmpeg:install:cancelled') {
        if (typeof message.status === 'object' && message.status !== null) {
          setFfmpegStatus(message.status as FfmpegRuntimeStatus)
        }
        setFfmpegProgress(null)
        setFfmpegError(null)
        setFfmpegDialogOpen(false)
        setFfmpegPendingFileName(null)
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
        if (changedJob.status === 'failed' && isCloudVoiceApiError(changedJob.errorCode ?? '')) {
          setApiError({
            code: changedJob.errorCode ?? 'FPT_PROVIDER_ERROR',
            message: changedJob.errorMessage ?? 'Không thể hoàn tất yêu cầu tạo giọng FPT.AI.',
            provider: 'FPT.AI',
          })
        }
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
        const progress = message.progress as { phase?: string, percent?: number, message?: string }
        const percent = Math.max(0, Math.min(100, Number(progress.percent ?? 0)))
        if (progress.phase === 'AI_STORAGE') {
          setAiStorageProgress({
            percent,
            message: String(progress.message ?? 'Đang xử lý dữ liệu AI local.'),
          })
        } else {
          setModelDownloadPercent(percent)
          if (percent >= 100) setModelDownloadPercent(null)
        }
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

      if (message.type === 'translation:settings:saved') {
        notify(
          'Đã lưu cấu hình dịch',
          'Bối cảnh, glossary và cấu hình Cloud đã được cập nhật.',
          'success',
        )
      }

      if (message.type === 'voice:settings:saved') {
        setProjectBusy(false)
        notify(
          'Đã lưu phân vai',
          'Cache audio chỉ được làm mới cho những câu bị đổi giọng.',
          'success',
        )
      }

      if (message.type === 'voice:cloud:settings:saved') {
        setProjectBusy(false)
        notify(
          'Đã lưu cấu hình FPT.AI',
          'API key được mã hóa theo tài khoản Windows và không nằm trong file project.',
          'success',
        )
      }

      if (message.type === 'voice:cloud:previewed') {
        setVoicePreviewBusy(false)
        setVoicePreviewDataUrl(String(message.audioDataUrl ?? ''))
        notify('FPT.AI đã sẵn sàng', 'API key và giọng đọc đã được kiểm tra thành công.', 'success')
      }

      if (message.type === 'voice:model:installed') {
        setProjectBusy(false)
        setModelDownloadPercent(null)
        notify('Đã cài bộ giọng', 'Model local đã sẵn sàng để tạo giọng.', 'success')
      }

      if (message.type === 'ai-storage:selected') {
        const destinationPath = String(message.destinationPath ?? '').trim()
        const currentPath = String(message.currentPath ?? '').trim()
        if (destinationPath && currentPath) {
          setAiStorageSelection({ destinationPath, currentPath })
          setAiStorageProgress(null)
        }
      }

      if (message.type === 'ai-storage:selection-cancelled') {
        setAiStorageSelection(null)
        setAiStorageProgress(null)
      }

      if (message.type === 'ai-storage:busy') {
        setProjectBusy(true)
        setAiStorageProgress({
          percent: 0,
          message: 'Đang kiểm tra thư mục và dung lượng trống.',
        })
      }

      if (message.type === 'ai-storage:saved') {
        setProjectBusy(false)
        setModelDownloadPercent(null)
        setAiStorageSelection(null)
        setAiStorageProgress(null)
        notify(
          'Đã đổi vị trí lưu AI',
          `Runtime, model và cache sẽ sử dụng ${String(message.destinationPath ?? 'thư mục mới')}.`,
          'success',
        )
      }

      if (message.type === 'ai-storage:discarded') {
        setProjectBusy(false)
        setAiStorageProgress(null)
        notify(
          'Đã bỏ bản migration tạm',
          'Vị trí AI hiện tại và dữ liệu nguồn vẫn được giữ nguyên.',
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
        setVoicePreviewBusy(false)
        setProjectError(errorMessage)
        if (code.startsWith('AI_STORAGE_')) {
          setAiStorageProgress(null)
        }
        const isApiError = isCloudTranslationApiError(code) || isCloudVoiceApiError(code)
        if (isApiError) {
          setApiError({
            code,
            message: errorMessage,
            provider: isCloudVoiceApiError(code) ? 'FPT.AI' : inferTranslationProvider(errorMessage),
          })
        }
        if (code === 'PROJECT_REQUIRED' || code.startsWith('PROJECT_')) {
          setProjectDialogOpen(true)
        }
        if (!isApiError) {
        notify(
          code === 'MEDIA_IMPORT_CANCELLED' ? 'Đã hủy nhập video' : 'Chưa thể thực hiện',
          errorMessage,
          'warning',
        )
        }
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
      postToHost('ffmpeg:status')
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

  const projectRemovalRegions = currentProject?.settings.originalSubtitleRemovalRegions
  const subtitleRemoval: SubtitleRemovalSettings = {
    enabled: currentProject?.settings.removeOriginalSubtitles ?? false,
    mode: currentProject?.settings.originalSubtitleRemovalMode ?? 'blur',
    x: currentProject?.settings.originalSubtitleRegionX ?? 0.05,
    y: currentProject?.settings.originalSubtitleRegionY ?? 0.70,
    width: currentProject?.settings.originalSubtitleRegionWidth ?? 0.90,
    height: currentProject?.settings.originalSubtitleRegionHeight ?? 0.16,
    regions: projectRemovalRegions?.length
      ? projectRemovalRegions
      : [{
          id: 'legacy',
          x: currentProject?.settings.originalSubtitleRegionX ?? 0.05,
          y: currentProject?.settings.originalSubtitleRegionY ?? 0.70,
          width: currentProject?.settings.originalSubtitleRegionWidth ?? 0.90,
          height: currentProject?.settings.originalSubtitleRegionHeight ?? 0.16,
        }],
  }

  const videoTransform: VideoTransformSettings = {
    flipHorizontal: currentProject?.settings.flipHorizontal ?? false,
    flipVertical: currentProject?.settings.flipVertical ?? false,
  }

  const updateVideoTransform = (next: VideoTransformSettings) => {
    setCurrentProject((current) => {
      if (!current) return current
      const settings = current.settings
      const horizontalChanged = (settings.flipHorizontal ?? false) !== next.flipHorizontal
      const verticalChanged = (settings.flipVertical ?? false) !== next.flipVertical
      if (!horizontalChanged && !verticalChanged) return current

      return {
        ...current,
        settings: {
          ...settings,
          flipHorizontal: next.flipHorizontal,
          flipVertical: next.flipVertical,
          originalSubtitleRegionX: horizontalChanged
            ? mirrorRegionCoordinate(settings.originalSubtitleRegionX, settings.originalSubtitleRegionWidth)
            : settings.originalSubtitleRegionX,
          originalSubtitleRegionY: verticalChanged
            ? mirrorRegionCoordinate(settings.originalSubtitleRegionY, settings.originalSubtitleRegionHeight)
            : settings.originalSubtitleRegionY,
          originalSubtitleRemovalRegions: settings.originalSubtitleRemovalRegions?.map((region) => ({
            ...region,
            x: horizontalChanged
              ? mirrorRegionCoordinate(region.x, region.width)
              : region.x,
            y: verticalChanged
              ? mirrorRegionCoordinate(region.y, region.height)
              : region.y,
          })),
        },
      }
    })
    if (hasNativeHost()) {
      postToHost('project:video-transform:update', next)
    }
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
        originalSubtitleRemovalRegions: next.regions,
      },
    }) : current)
    if (hasNativeHost()) {
      postToHost('project:subtitle-removal:update', next)
    }
  }

  const subtitleStyle: SubtitleStyleSettings = currentProject?.settings.subtitleStyle
    ?? defaultSubtitleStyle

  const vietnameseSubtitlesEnabled = currentProject?.settings.vietnameseSubtitlesEnabled ?? true

  const updateVietnameseSubtitlesEnabled = (enabled: boolean) => {
    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        vietnameseSubtitlesEnabled: enabled,
      },
    }) : current)
    if (hasNativeHost()) {
      postToHost('project:vietnamese-subtitles:update', { enabled })
    }
  }

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

  const updateSubtitleSegment = (cueId: string, original: string, translated: string) => {
    if (hasNativeHost()) {
      setSubtitleBusy(true)
      postToHost('subtitle:update', { cueId, original, translated })
      return
    }

    const updateLocalSegment = (segment: SubtitleSegment): SubtitleSegment => {
      if (segment.cueId !== cueId) return segment
      const voiceStillMatches = segment.translated.trim() === translated.trim()
        && Boolean(segment.hasVoice)
      return {
        ...segment,
        original,
        translated,
        hasVoice: voiceStillMatches,
        status: !translated.trim()
          ? 'review'
          : voiceStillMatches ? 'translated' : 'missing-audio',
      }
    }

    setSegments((current) => current.map(updateLocalSegment))
    setCurrentProject((current) => current ? ({
      ...current,
      voicePlaybackUrl: null,
      subtitles: current.subtitles.map(updateLocalSegment),
    }) : current)
    notify(
      'Đã cập nhật bản dịch',
      'Chế độ xem trước đã cập nhật. Audio cần được tạo lại nếu nội dung đã thay đổi.',
      'success',
    )
  }

  const updateVoiceSettings = (
    defaultVoiceId: string,
    speakerVoiceIds: Record<string, string>,
    speed = currentProject?.settings.voice.speed ?? 0,
  ) => {
    setProjectBusy(true)
    if (hasNativeHost()) {
      postToHost('project:voice-settings:update', { defaultVoiceId, speakerVoiceIds, speed })
      return
    }

    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        voice: { ...current.settings.voice, defaultVoiceId, speakerVoiceIds, speed },
      },
    }) : current)
    setProjectBusy(false)
  }

  const installVoice = (voiceId: string) => {
    setProjectBusy(true)
    if (hasNativeHost()) {
      postToHost('voice:model:install', { voiceId })
      return
    }

    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        voice: {
          ...current.settings.voice,
          voices: current.settings.voice.voices.map((voice) => (
            !voice.isCloud && voice.engine === (voiceId.startsWith('vieneu:') ? 'vieneu' : 'piper')
              ? { ...voice, installed: true }
              : voice
          )),
        },
      },
    }) : current)
    setProjectBusy(false)
  }

  const openVoiceSelection = () => {
    if (!currentProject?.settings.voice.voices.length) {
      notify('Chưa có giọng đọc', 'Không tìm thấy giọng local hoặc online để tạo audio.', 'warning')
      return
    }
    setVoicePreviewDataUrl(null)
    setVoiceSelectionOpen(true)
  }

  const startVoiceSynthesis = (voiceId: string, speed: number, apiKey?: string) => {
    setVoiceSelectionOpen(false)
    setVoicePreviewDataUrl(null)
    setJobBusy(true)
    if (hasNativeHost()) {
      postToHost('job:voice:synthesize', { voiceId, speed, apiKey })
      return
    }

    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        voice: {
          ...current.settings.voice,
          defaultVoiceId: voiceId,
          speed,
          fptApiKeyConfigured: current.settings.voice.fptApiKeyConfigured || Boolean(apiKey),
        },
      },
    }) : current)
    setJobBusy(false)
    notify('Đã chọn giọng đọc', 'Bản xem trước đã ghi nhận giọng mặc định mới.', 'success')
  }

  const previewFptVoice = (voiceId: string, speed: number, apiKey?: string) => {
    setVoicePreviewBusy(true)
    setVoicePreviewDataUrl(null)
    if (hasNativeHost()) {
      postToHost('voice:cloud:preview', { voiceId, speed, apiKey })
      return
    }

    window.setTimeout(() => {
      setVoicePreviewBusy(false)
      notify('Cần chạy trong ứng dụng desktop', 'Bản preview web không thể gọi FPT.AI vì API key chỉ lưu ở Windows host.', 'warning')
    }, 300)
  }

  const updateFptVoiceCredential = (apiKey?: string, clearApiKey = false) => {
    setProjectBusy(true)
    if (hasNativeHost()) {
      postToHost('project:voice-cloud-settings:update', { apiKey, clearApiKey })
      return
    }

    setCurrentProject((current) => current ? ({
      ...current,
      settings: {
        ...current.settings,
        voice: {
          ...current.settings.voice,
          fptApiKeyConfigured: clearApiKey ? false : Boolean(apiKey) || current.settings.voice.fptApiKeyConfigured,
        },
      },
    }) : current)
    setProjectBusy(false)
  }

  const updateSubtitleVoice = (cueId: string, speaker: string, voiceId: string | null) => {
    if (hasNativeHost()) {
      setSubtitleBusy(true)
      postToHost('subtitle:voice:update', { cueId, speaker, voiceId })
      return
    }

    const settings = currentProject?.settings.voice ?? defaultVoiceSettings
    const resolvedVoiceId = voiceId
      || settings.speakerVoiceIds[speaker]
      || settings.defaultVoiceId
    const update = (segment: SubtitleSegment): SubtitleSegment => segment.cueId === cueId
      ? { ...segment, speaker, voiceId, resolvedVoiceId, hasVoice: false, status: 'missing-audio' }
      : segment
    setSegments((current) => current.map(update))
    setCurrentProject((current) => current ? ({
      ...current,
      voicePlaybackUrl: null,
      subtitles: current.subtitles.map(update),
    }) : current)
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
          ffmpegStatus={ffmpegStatus}
          ffmpegProgress={ffmpegProgress}
          onRefresh={() => postToHost('auth:refresh')}
          onLogout={() => postToHost('auth:logout')}
          onManageFfmpeg={() => {
            if (jobBusy || importState.active) {
              notify('Công cụ video đang được sử dụng', 'Hãy chờ tác vụ video hiện tại hoàn tất trước khi cài lại FFmpeg.', 'warning')
              return
            }
            setFfmpegForce(ffmpegStatus.ready)
            setFfmpegPendingFileName(null)
            setFfmpegError(null)
            setFfmpegDialogOpen(true)
          }}
          onSelectFfmpegFolder={() => {
            if (jobBusy || importState.active) {
              notify('Công cụ video đang được sử dụng', 'Hãy chờ tác vụ video hiện tại hoàn tất trước khi đổi thư mục FFmpeg.', 'warning')
              return
            }
            postToHost('ffmpeg:folder:select')
          }}
          onOpenFfmpegFolder={() => postToHost('ffmpeg:folder:open')}
        />
      ) : activeNav === 'voice' || activeNav === 'library' ? (
        <VoiceWorkspace
          mode={activeNav}
          settings={currentProject?.settings.voice ?? null}
          segments={segments}
          busy={projectBusy || subtitleBusy || jobBusy}
          downloadPercent={modelDownloadPercent}
          storage={currentProject?.aiStorage ?? null}
          onSave={updateVoiceSettings}
          onSaveFptCredential={updateFptVoiceCredential}
          onInstall={installVoice}
          onSynthesize={openVoiceSelection}
          onSelectStorage={() => postToHost('ai-storage:select')}
          onResumeStorage={(destinationPath) => {
            const currentPath = currentProject?.aiStorage.rootPath
            if (!currentPath || projectBusy) return
            setAiStorageSelection({ currentPath, destinationPath })
            setProjectBusy(true)
            setAiStorageProgress({
              percent: 0,
              message: 'Đang tiếp tục migration còn dang dở.',
            })
            postToHost('ai-storage:change', { destinationPath, migrateExisting: true })
          }}
          onDiscardPendingStorage={() => {
            if (projectBusy) return
            setProjectBusy(true)
            postToHost('ai-storage:discard-pending')
          }}
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
            translationSettings={currentProject?.settings?.translation ?? defaultTranslationSettings}
            subtitleCount={segments.length}
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
            onTranslationSettingsChange={(settings, apiKey, clearApiKey) => {
              if (hasNativeHost()) {
                postToHost('project:translation-settings:update', {
                  ...settings,
                  apiKey,
                  clearApiKey: Boolean(clearApiKey),
                })
                return
              }
              setCurrentProject((current) => current ? ({
                ...current,
                settings: {
                  ...current.settings,
                  translationModelId: settings.modelId,
                  translation: {
                    ...settings,
                    apiKeyConfigured: clearApiKey ? false : settings.apiKeyConfigured || Boolean(apiKey),
                  },
                },
              }) : current)
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
              const failedJob = currentProject?.jobs.find((job) => job.jobId === jobId)
              if (failedJob?.jobType === 'TRANSLATE_CLOUD' || failedJob?.jobType === 'TRANSLATE_LOCAL') {
                setTranslationRetryJob(failedJob)
                return
              }
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
            busy={subtitleBusy || jobBusy}
            playing={playing}
            currentTime={currentTime}
            playbackRate={playbackRate}
            originalAudioEnabled={audioSettings.originalAudioEnabled && sourceAudioAvailable}
            sourceVolume={audioSettings.originalAudioVolumePercent}
            voiceAudioEnabled={audioSettings.vietnameseVoiceEnabled && voicePreviewAvailable}
            voiceVolume={audioSettings.vietnameseVoiceVolumePercent}
            voicePlaybackUrl={currentProject?.voicePlaybackUrl ?? null}
            vietnameseSubtitlesEnabled={vietnameseSubtitlesEnabled}
            subtitleText={segments.find((segment) => (
              currentTime >= segment.start && currentTime < segment.end
            ))?.translated ?? ''}
            subtitleRemoval={subtitleRemoval}
            subtitleStyle={subtitleStyle}
            videoTransform={videoTransform}
            onTogglePlay={togglePlayback}
            onTimeUpdate={setCurrentTime}
            onPlaybackEnded={() => setPlaying(false)}
            onPlaybackError={handlePlaybackError}
            onVoicePlaybackError={handleVoicePlaybackError}
            onVietnameseSubtitlesEnabledChange={updateVietnameseSubtitlesEnabled}
            onOpenVideo={openVideo}
            onDropVideo={loadBrowserFile}
            onSubtitleRemovalChange={updateSubtitleRemoval}
            onSubtitleStyleChange={updateSubtitleStyle}
            onVideoTransformChange={updateVideoTransform}
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
              openVoiceSelection()
            }}
            onUpdateSegment={updateSubtitleSegment}
            voices={currentProject?.settings.voice.voices ?? []}
            onUpdateVoice={updateSubtitleVoice}
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
          flipHorizontal={videoTransform.flipHorizontal}
          flipVertical={videoTransform.flipVertical}
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
          onUpdateSegment={updateSubtitleSegment}
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

      <FfmpegSetupDialog
        open={ffmpegDialogOpen}
        status={ffmpegStatus}
        progress={ffmpegProgress}
        error={ffmpegError}
        pendingFileName={ffmpegPendingFileName}
        force={ffmpegForce}
        onInstall={() => {
          setFfmpegError(null)
          postToHost('ffmpeg:install', { force: ffmpegForce })
        }}
        onSelectFolder={() => postToHost('ffmpeg:folder:select')}
        onCancel={() => {
          postToHost('ffmpeg:install:cancel')
          setFfmpegDialogOpen(false)
          setFfmpegError(null)
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

      <TranslationRetryDialog
        open={translationRetryJob !== null}
        errorMessage={translationRetryJob?.errorMessage}
        completedCues={translationRetryJob?.translationMetrics?.completedCues}
        totalPendingCues={translationRetryJob?.translationMetrics?.totalPendingCues}
        onClose={() => setTranslationRetryJob(null)}
        onSelect={(translationMode) => {
          const jobId = translationRetryJob?.jobId
          setTranslationRetryJob(null)
          if (!jobId) return
          setJobBusy(true)
          postToHost('job:retry', { jobId, translationMode })
        }}
      />

      <ApiErrorDialog
        open={apiError !== null}
        provider={apiError?.provider ?? 'dịch vụ AI cloud'}
        code={apiError?.code ?? 'TRANSLATION_PROVIDER_ERROR'}
        message={apiError?.message ?? 'Không thể kết nối dịch vụ dịch cloud.'}
        onClose={() => setApiError(null)}
      />

      <VoiceSelectionDialog
        open={voiceSelectionOpen}
        settings={currentProject?.settings.voice ?? null}
        busy={projectBusy || jobBusy}
        previewBusy={voicePreviewBusy}
        previewAudioDataUrl={voicePreviewDataUrl}
        downloadPercent={modelDownloadPercent}
        onClose={() => {
          if (!projectBusy && !jobBusy && !voicePreviewBusy) {
            setVoiceSelectionOpen(false)
            setVoicePreviewDataUrl(null)
          }
        }}
        onInstall={installVoice}
        onPreview={previewFptVoice}
        onConfirm={startVoiceSynthesis}
      />

      <AiStorageDialog
        selection={aiStorageSelection}
        busy={aiStorageProgress !== null}
        progress={aiStorageProgress}
        onClose={() => {
          if (!projectBusy) {
            setAiStorageSelection(null)
            setAiStorageProgress(null)
          }
        }}
        onConfirm={(migrateExisting) => {
          if (!aiStorageSelection || projectBusy) return
          setProjectBusy(true)
          setAiStorageProgress({
            percent: 0,
            message: 'Đang kiểm tra thư mục và dung lượng trống.',
          })
          postToHost('ai-storage:change', {
            destinationPath: aiStorageSelection.destinationPath,
            migrateExisting,
          })
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
