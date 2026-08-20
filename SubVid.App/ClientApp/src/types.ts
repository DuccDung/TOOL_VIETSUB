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
  translationMetrics?: TranslationJobMetrics | null
  voiceMetrics?: VoiceSynthesisJobMetrics | null
}

export type TranslationJobMetrics = {
  inputTokens: number
  outputTokens: number
  cachedInputTokens: number
  apiRequests: number
  retryRequests: number
  cacheHitScenes: number
  translatedScenes: number
  reviewedCues: number
  autoRepairedCues: number
  skippedCues: number
  completedCues: number
  totalPendingCues: number
}

export type VoiceSynthesisJobMetrics = {
  totalCharacters: number
  submittedCharacters: number
  apiRequests: number
  retryRequests: number
  cacheHitCues: number
  completedCues: number
  totalCues: number
  timingWarningCues?: number
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
  aiStorage: AiStorageInfo
  video: VideoInfo | null
  voicePlaybackUrl: string | null
  voicePlaybackStale: boolean
  subtitles: SubtitleSegment[]
  jobs: LocalJobInfo[]
}

export type AiStorageInfo = {
  rootPath: string
  freeBytes: number
  usesLegacyLocation: boolean
  recommendedPath: string
  pendingMigrationPath: string | null
}

export type ProjectSettingsInfo = {
  speechModel: string
  ocrLanguageCode: 'auto' | 'en' | 'zh' | string
  translationModelId: string
  translation: TranslationSettingsInfo
  voice: VoiceSettingsInfo
  originalAudioEnabled: boolean
  originalAudioVolumePercent: number
  vietnameseVoiceEnabled: boolean
  vietnameseVoiceVolumePercent: number
  vietnameseSubtitlesEnabled: boolean
  flipHorizontal: boolean
  flipVertical: boolean
  removeOriginalSubtitles: boolean
  originalSubtitleRemovalMode: 'blur' | 'cover'
  originalSubtitleRegionX: number
  originalSubtitleRegionY: number
  originalSubtitleRegionWidth: number
  originalSubtitleRegionHeight: number
  originalSubtitleRemovalRegions?: SubtitleRemovalRegion[]
  subtitleStyle: SubtitleStyleSettings
}

export type VoiceSettingsInfo = {
  defaultVoiceId: string
  speakerVoiceIds: Record<string, string>
  voices: VoiceInfo[]
  speed: number
  timelineMaximumTempo: number
  timelinePreferredTempo: number
  timelineMaximumBorrowMilliseconds: number
  trimSilenceEnabled: boolean
  phraseSynthesisEnabled: boolean
  timelineSlowdownEnabled: boolean
  fptApiKeyConfigured: boolean
  estimatedCharacters: number
}

export type VoiceInfo = {
  voiceId: string
  engine: 'piper' | 'vieneu' | 'fpt' | string
  displayName: string
  gender: string
  region: string
  style: string
  modelVersion: string
  license: string
  installed: boolean
  isCloud: boolean
  requiresInstall: boolean
  installState: 'ONLINE' | 'READY' | 'MISSING' | 'REPAIR_REQUIRED' | string
}

export type TranslationSettingsInfo = {
  provider: 'local' | 'openai' | 'gemini' | 'deepseek' | 'groq'
  modelId: string
  qualityMode: 'fast' | 'balanced' | 'high'
  reviewEnabled: boolean
  fallbackToLocal: boolean
  apiKeyConfigured: boolean
  projectContext: string
  characterInstructions: string
  styleInstructions: string
  glossaryText: string
  translationMemoryCount: number
}

export type SubtitleRemovalSettings = {
  enabled: boolean
  mode: 'blur' | 'cover'
  x: number
  y: number
  width: number
  height: number
  regions: SubtitleRemovalRegion[]
}

export type SubtitleRemovalRegion = {
  id: string
  x: number
  y: number
  width: number
  height: number
}

export type VideoTransformSettings = {
  flipHorizontal: boolean
  flipVertical: boolean
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

export type FfmpegRuntimeStatus = {
  state: 'MISSING' | 'READY' | 'INSTALLING' | 'ERROR'
  ready: boolean
  managed: boolean
  source: 'NONE' | 'MANAGED' | 'CUSTOM' | 'ENVIRONMENT' | 'SYSTEM'
  version: string | null
  targetVersion: string
  ffmpegPath: string | null
  ffprobePath: string | null
  installDirectory: string
  downloadBytes: number
  license: string
  sourceUrl: string
}

export type FfmpegInstallProgress = {
  phase: 'DOWNLOAD' | 'EXTRACT' | 'VERIFY' | 'INSTALL' | 'READY'
  percent: number
  message: string
  bytesProcessed: number
  totalBytes: number
}

export type SubtitleSegment = {
  cueId: string
  id: number
  start: number
  end: number
  original: string
  translated: string
  speaker?: string
  voiceId?: string | null
  resolvedVoiceId?: string
  status: 'translated' | 'review' | 'missing-audio' | 'invalid-translation'
  hasVoice?: boolean
  translationConfidence?: number | null
  translationWarnings?: string[]
  voiceTiming?: VoiceTimingInfo | null
  voicePhrase?: VoicePhraseInfo | null
  voiceBoundaryAfter?: VoiceBoundaryInfo | null
}

export type VoiceBoundaryMode = 'AUTO' | 'JOIN' | 'BREAK'

export type VoicePhraseInfo = {
  phraseId: string
  startCueNumber: number
  endCueNumber: number
  cueCount: number
  hasAudio: boolean
  needsRegeneration: boolean
}

export type VoiceBoundaryInfo = {
  nextCueId: string
  mode: VoiceBoundaryMode
  effectiveMode: 'JOIN' | 'BREAK'
  canJoin: boolean
  constraintMessage: string | null
}

export type VoiceTimingInfo = {
  rawDurationSeconds: number
  sourceDurationSeconds: number
  targetDurationSeconds: number
  effectiveWindowSeconds: number
  renderDurationSeconds: number
  leadingSilenceSeconds: number
  trailingSilenceSeconds: number
  trimStartSeconds: number
  trimEndSeconds: number
  borrowedGapSeconds: number
  requiredTempo: number
  appliedTempo: number | null
  paddingSeconds: number
  baseTtsSpeed: number
  appliedTtsSpeed: number
  phraseId: string | null
  resolutionAction: string
  status: 'NATURAL' | 'PADDED' | 'GAP_FITTED' | 'COMPRESSED' | 'REVIEW_REQUIRED' | 'INVALID'
  severity: 'INFO' | 'WARNING' | 'ERROR'
  message: string
  suggestedMaximumCharacters: number | null
  analyzedAtUtc: string
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

export type PlanCloudOptionInfo = {
  providerCode: string
  allocationMode: string
  monthlyTokenLimit: number
  allowedModels: string[]
  allowSharedFallback: boolean
}

export type PlanCatalogItemInfo = {
  code: string
  displayName: string
  description: string | null
  priceAmount: number
  currencyCode: string
  billingPeriodDays: number
  monthlyQuotaMinutes: number | null
  maxVideoMinutes: number | null
  features: string[]
  cloudOptions: PlanCloudOptionInfo[]
}

export type PurchaseCheckoutInfo = {
  orderId: string
  orderNumber: string
  orderStatus: string
  paymentStatus: string
  planCode: string
  planName: string
  transactionCode: string
  bankName: string
  bankShortName: string
  accountNumber: string
  accountName: string
  transferContent: string
  qrImageUrl: string
  amount: number
  currency: string
  createdAtUtc: string
  expiresAtUtc: string
  paidAtUtc: string | null
  isPaid: boolean
  isExpired: boolean
  message: string
  reusedExistingOrder: boolean
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
