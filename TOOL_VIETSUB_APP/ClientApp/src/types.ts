export type VideoInfo = {
  fileName: string
  extension: string
  sizeBytes: number
  durationSeconds: number
  width?: number
  height?: number
  framesPerSecond?: number
  videoCodec?: string | null
  audioCodec?: string | null
  audioTrackCount?: number
  hasAudio?: boolean
  importMode?: 'COPY' | 'LINK' | string
  sha256?: string
  playbackUrl?: string
}

export type LocalJobStatus =
  | 'pending'
  | 'running'
  | 'paused'
  | 'interrupted'
  | 'completed'
  | 'failed'
  | 'cancelled'

export type LocalJobInfo = {
  jobId: string
  jobType: string
  status: LocalJobStatus
  progressPercent: number
  currentStep: string | null
  errorCode: string | null
  errorMessage: string | null
}

export type ProjectSummaryInfo = {
  projectId: string
  name: string
  status: string
  updatedAtUtc: string
  needsRecovery: boolean
  sourceFileName: string | null
  durationSeconds: number | null
}

export type ProjectInfo = {
  projectId: string
  name: string
  status: string
  needsRecovery: boolean
  serverSynchronized: boolean
  updatedAtUtc: string
  sourceLanguageCode: 'auto' | 'en' | 'zh' | string
  targetLanguageCode: string
  settings: ProjectSettingsInfo
  video: VideoInfo | null
  voicePlaybackUrl: string | null
  subtitles: SubtitleSegment[]
  jobs: LocalJobInfo[]
}

export type ProjectSettingsInfo = {
  speechModel: string
  ocrLanguageCode: 'auto' | 'en' | 'zh' | string
  translationModelId: string
  originalAudioEnabled: boolean
  originalAudioVolumePercent: number
  vietnameseVoiceEnabled: boolean
  vietnameseVoiceVolumePercent: number
  removeOriginalSubtitles: boolean
  originalSubtitleRemovalMode: 'blur' | 'cover'
  originalSubtitleRegionX: number
  originalSubtitleRegionY: number
  originalSubtitleRegionWidth: number
  originalSubtitleRegionHeight: number
  subtitleStyle: SubtitleStyleSettings
}

export type SubtitleRemovalSettings = {
  enabled: boolean
  mode: 'blur' | 'cover'
  x: number
  y: number
  width: number
  height: number
}

export type SubtitleStyleSettings = {
  presetId: 'readable' | 'outline' | 'tiktok' | 'cinematic' | 'yellow' | 'minimal' | 'custom'
  fontFamily: 'Arial' | 'Segoe UI' | 'Tahoma' | 'Verdana' | 'Times New Roman'
  fontSizePercent: number
  bold: boolean
  textColor: string
  outlineColor: string
  outlineSize: number
  shadowSize: number
  backgroundMode: 'none' | 'box'
  backgroundColor: string
  backgroundOpacity: number
  horizontalAlignment: 'left' | 'center' | 'right'
  verticalPosition: 'top' | 'middle' | 'bottom' | 'custom'
  positionXPercent: number
  positionYPercent: number
  maxWidthPercent: number
  maxLines: 1 | 2 | 3
}

export type MediaImportState = {
  active: boolean
  fileName: string | null
  mode: 'COPY' | 'LINK'
  percent: number
  bytesProcessed: number
  totalBytes: number
  megabytesPerSecond: number
}

export type SubtitleSegment = {
  cueId: string
  id: number
  start: number
  end: number
  original: string
  translated: string
  status: 'translated' | 'review' | 'missing-audio' | 'invalid-translation'
  hasVoice?: boolean
}

export type ToastMessage = {
  id: number
  title: string
  description: string
  tone?: 'info' | 'success' | 'warning'
}

export type AccountInfo = {
  userId: string
  email: string
  displayName: string
  role: string
  status: string
  emailConfirmed: boolean
  createdAtUtc: string
  lastLoginAtUtc: string | null
}

export type PlanInfo = {
  code: string
  displayName: string
  description: string | null
  status: string
  startsAtUtc: string | null
  endsAtUtc: string | null
}

export type QuotaInfo = {
  monthlyMinutes: number | null
  usedMinutes: number
  reservedMinutes: number
  remainingMinutes: number | null
  maxVideoMinutes: number | null
  periodStartsAtUtc: string
  periodEndsAtUtc: string
}

export type EntitlementsInfo = {
  plan: PlanInfo
  quota: QuotaInfo
  features: string[]
  evaluatedAtUtc: string
}

export type UsageHistoryItem = {
  eventId: string
  operationCode: string
  quantity: number
  unitCode: string
  occurredAtUtc: string
  projectId: string | null
  jobId: string | null
}

export type UsageHistory = {
  page: number
  pageSize: number
  totalCount: number
  items: UsageHistoryItem[]
}

export type AuthState = {
  status: 'loading' | 'authenticated' | 'unauthenticated' | 'error'
  account: AccountInfo | null
  entitlements: EntitlementsInfo | null
  history: UsageHistory | null
  errorCode?: string | null
  errorMessage?: string | null
}

export type RegistrationChallenge = {
  challengeId: string
  maskedEmail: string
  expiresAtUtc: string
  resendAtUtc: string
  resendsRemaining: number
}

export type RegistrationState = {
  challenge: RegistrationChallenge | null
  busy: boolean
  operation: 'start' | 'verify' | 'resend' | null
  errorCode: string | null
  errorMessage: string | null
}
