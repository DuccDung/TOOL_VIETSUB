import { useState } from 'react'
import {
  AudioWaveform,
  Captions,
  CheckCircle2,
  Eraser,
  Languages,
  LoaderCircle,
  Pause,
  Play,
  Plus,
  RotateCcw,
  ScanLine,
  Sparkles,
  Square,
  Trash2,
  Type,
} from 'lucide-react'
import type {
  LocalJobInfo,
  SubtitleRemovalSettings,
  SubtitleStyleSettings,
  TranslationSettingsInfo,
} from '../types'
import {
  createSubtitleRemovalRegion,
  getSubtitleRemovalRegions,
  maxSubtitleRemovalRegions,
  withSubtitleRemovalRegions,
} from '../lib/subtitleRemoval'
import { SubtitleStyleEditor } from './SubtitleStyleEditor'
import { TranslationSettingsEditor } from './TranslationSettingsEditor'
import { SectionCard, SegmentTab, SelectField, Toggle } from './Ui'

type SettingsPanelProps = {
  sourceLanguageCode: string
  ocrLanguageCode: string
  translationSettings: TranslationSettingsInfo
  subtitleCount: number
  subtitleRemoval: SubtitleRemovalSettings
  subtitleStyle: SubtitleStyleSettings
  canPrepareAudio: boolean
  canRunOcr: boolean
  audioJob: LocalJobInfo | null
  transcriptionJob: LocalJobInfo | null
  ocrJob: LocalJobInfo | null
  pipelineJob: LocalJobInfo | null
  modelDownloadPercent: number | null
  jobBusy: boolean
  onLanguageSettingsChange: (sourceLanguageCode: string, ocrLanguageCode: string) => void
  onTranslationSettingsChange: (
    settings: TranslationSettingsInfo,
    apiKey?: string,
    clearApiKey?: boolean,
  ) => void
  onSubtitleRemovalChange: (settings: SubtitleRemovalSettings) => void
  onSubtitleStyleChange: (style: SubtitleStyleSettings) => void
  onPrepareAudio: () => void
  onTranscribe: () => void
  onOcr: () => void
  onPauseJob: (jobId: string) => void
  onResumeJob: (jobId: string) => void
  onRetryJob: (jobId: string) => void
  onCancelJob: (jobId: string) => void
}

const terminalStates = ['completed', 'cancelled']

const formatTokenCount = (value: number) => new Intl.NumberFormat('vi-VN').format(Math.max(0, value))

export function SettingsPanel({
  sourceLanguageCode,
  ocrLanguageCode,
  translationSettings,
  subtitleCount,
  subtitleRemoval,
  subtitleStyle,
  canPrepareAudio,
  canRunOcr,
  audioJob,
  transcriptionJob,
  ocrJob,
  pipelineJob,
  modelDownloadPercent,
  jobBusy,
  onLanguageSettingsChange,
  onTranslationSettingsChange,
  onSubtitleRemovalChange,
  onSubtitleStyleChange,
  onPrepareAudio,
  onTranscribe,
  onOcr,
  onPauseJob,
  onResumeJob,
  onRetryJob,
  onCancelJob,
}: SettingsPanelProps) {
  const [mode, setMode] = useState<'subtitle' | 'dubbing'>('subtitle')
  const [speechCollapsed, setSpeechCollapsed] = useState(false)
  const [translationCollapsed, setTranslationCollapsed] = useState(false)
  const [removalCollapsed, setRemovalCollapsed] = useState(false)
  const [styleCollapsed, setStyleCollapsed] = useState(false)
  const [ocrEnabled, setOcrEnabled] = useState(false)
  const jobs = [pipelineJob, ocrJob, transcriptionJob, audioJob]
    .filter((job): job is LocalJobInfo => job !== null)
    .filter((job, index, items) => items.findIndex((item) => item.jobId === job.jobId) === index)
  const displayJob = jobs.find((job) => ['pending', 'running', 'paused', 'interrupted'].includes(job.status))
    ?? jobs[0]
  const canStart = !displayJob || terminalStates.includes(displayJob.status)
  const removalRegions = getSubtitleRemovalRegions(subtitleRemoval)

  const addRemovalRegion = () => {
    if (jobBusy || removalRegions.length >= maxSubtitleRemovalRegions) return
    const region = createSubtitleRemovalRegion(removalRegions.length)
    onSubtitleRemovalChange(withSubtitleRemovalRegions(
      { ...subtitleRemoval, enabled: true },
      [...removalRegions, region],
    ))
  }

  const removeRemovalRegion = (regionId: string) => {
    if (jobBusy || removalRegions.length <= 1) return
    onSubtitleRemovalChange(withSubtitleRemovalRegions(
      subtitleRemoval,
      removalRegions.filter((region) => region.id !== regionId),
    ))
  }

  const jobTitle = () => {
    if (!displayJob) return 'Chưa nhận dạng giọng nói'
    const jobName: Record<string, string> = {
      EXTRACT_AUDIO: 'Chuẩn hóa audio',
      TRANSCRIBE_LOCAL: 'Nhận dạng giọng nói',
      OCR_LOCAL: 'Nhận dạng phụ đề cứng',
      TRANSLATE_LOCAL: 'Dịch sang tiếng Việt',
      TRANSLATE_CLOUD: 'Dịch sang tiếng Việt bằng cloud',
      SYNTHESIZE_VOICE_LOCAL: 'Tạo giọng Việt',
      SYNTHESIZE_VOICE_CLOUD: 'Tạo giọng Việt bằng FPT.AI',
      EXPORT_VIDEO_LOCAL: 'Đồng bộ và xuất video',
    }
    const name = jobName[displayJob.jobType] ?? 'Xử lý local'
    if (displayJob.status === 'running') {
      return `Đang ${name.toLocaleLowerCase('vi')}`
    }
    if (displayJob.status === 'paused') return 'Đã tạm dừng'
    if (displayJob.status === 'interrupted') return 'Bị gián đoạn'
    if (displayJob.status === 'failed') return 'Xử lý thất bại'
    if (displayJob.status === 'cancelled') return 'Đã hủy công việc'
    if (displayJob.status === 'pending') return 'Đang chờ xử lý'
    return `${name} đã hoàn tất`
  }

  return (
    <aside className="panel settings-panel" aria-label="Thiết lập xử lý">
      <div className="panel-mode-tabs" role="tablist" aria-label="Chế độ biên tập">
        <SegmentTab
          active={mode === 'subtitle'}
          icon={<Captions size={16} />}
          label="Phụ đề"
          onClick={() => setMode('subtitle')}
        />
        <SegmentTab
          active={mode === 'dubbing'}
          icon={<AudioWaveform size={16} />}
          label="Thuyết minh"
          onClick={() => setMode('dubbing')}
        />
      </div>

      <div className="panel-scroll">
        <SectionCard
          title="NHẬN DẠNG GIỌNG NÓI"
          icon={<AudioWaveform size={16} />}
          collapsed={speechCollapsed}
          onToggle={() => setSpeechCollapsed((value) => !value)}
          badge="BƯỚC 1"
        >
          <SelectField
            label="NGÔN NGỮ GỐC"
            value={sourceLanguageCode}
            disabled={jobBusy}
            helper="Chọn tiếng Trung để Whisper, OCR và bộ dịch dùng đúng model"
            onChange={(event) => onLanguageSettingsChange(event.target.value, ocrLanguageCode)}
          >
            {!['auto', 'zh', 'en'].includes(sourceLanguageCode) ? (
              <option value={sourceLanguageCode}>Đã phát hiện: {sourceLanguageCode} · Chưa hỗ trợ dịch</option>
            ) : null}
            <option value="auto">Tự động phát hiện · Khuyến nghị</option>
            <option value="zh">Tiếng Trung</option>
            <option value="en">Tiếng Anh</option>
          </SelectField>

          <SelectField
            label="MÔ HÌNH NHẬN DẠNG"
            defaultValue="balanced"
            helper="Whisper Base chạy hoàn toàn trên máy"
          >
            <option value="balanced">Whisper Base · Khuyến nghị</option>
          </SelectField>

          <Toggle
            checked={ocrEnabled}
            label="Hiện vùng OCR"
            description="Dùng khi video có phụ đề cứng"
            icon={<ScanLine size={17} />}
            onChange={setOcrEnabled}
          />

          <SelectField
            label="NGÔN NGỮ OCR"
            value={ocrLanguageCode}
            disabled={jobBusy}
            helper="Tự động sẽ dùng ngôn ngữ gốc đã chọn ở trên"
            onChange={(event) => onLanguageSettingsChange(sourceLanguageCode, event.target.value)}
          >
            <option value="auto">Theo ngôn ngữ gốc</option>
            <option value="zh">Tiếng Trung · PaddleOCR V5</option>
            <option value="en">Tiếng Anh · PaddleOCR V5</option>
          </SelectField>
        </SectionCard>

        <SectionCard
          title="DỊCH SANG TIẾNG VIỆT"
          icon={<Languages size={16} />}
          collapsed={translationCollapsed}
          onToggle={() => setTranslationCollapsed((value) => !value)}
          badge="BƯỚC 2"
        >
          <TranslationSettingsEditor
            settings={translationSettings}
            sourceLanguageCode={sourceLanguageCode}
            subtitleCount={subtitleCount}
            disabled={jobBusy}
            onSave={onTranslationSettingsChange}
          />

          <div className="panel-tip">
            <Sparkles size={16} />
            <div>
              <strong>Dịch theo cảnh, giữ đúng từng cue</strong>
              <p>Cloud dùng câu trước/sau, glossary và bản dịch đã duyệt; local vẫn là chế độ riêng tư.</p>
            </div>
          </div>
        </SectionCard>

        <SectionCard
          title="KIỂU PHỤ ĐỀ VIỆT"
          icon={<Type size={16} />}
          collapsed={styleCollapsed}
          onToggle={() => setStyleCollapsed((value) => !value)}
          badge="TÙY CHỈNH"
        >
          <SubtitleStyleEditor
            style={subtitleStyle}
            disabled={jobBusy}
            onChange={onSubtitleStyleChange}
          />
          <div className="panel-tip panel-tip--subtitle-style">
            <Type size={16} />
            <div>
              <strong>Có thể kéo chữ ngay trên preview</strong>
              <p>Phím mũi tên di chuyển chính xác; Shift cộng phím mũi tên để di chuyển nhanh hơn.</p>
            </div>
          </div>
        </SectionCard>

        <SectionCard
          title="XÓA PHỤ ĐỀ GỐC"
          icon={<Eraser size={16} />}
          collapsed={removalCollapsed}
          onToggle={() => setRemovalCollapsed((value) => !value)}
          badge="XUẤT VIDEO"
        >
          <Toggle
            checked={subtitleRemoval.enabled}
            disabled={jobBusy}
            label="Che phụ đề Trung đã dính vào video"
            description="Áp dụng trên preview và khi xuất; video nguồn không bị thay đổi"
            icon={<Eraser size={17} />}
            onChange={(enabled) => onSubtitleRemovalChange({ ...subtitleRemoval, enabled })}
          />

          <SelectField
            label="KIỂU XỬ LÝ"
            value={subtitleRemoval.mode}
            disabled={!subtitleRemoval.enabled || jobBusy}
            helper="Làm mờ phù hợp nền chuyển động; nền tối che chữ mạnh hơn"
            onChange={(event) => onSubtitleRemovalChange({
              ...subtitleRemoval,
              mode: event.target.value as SubtitleRemovalSettings['mode'],
            })}
          >
            <option value="blur">Làm mờ vùng phụ đề · Khuyến nghị</option>
            <option value="cover">Nền tối bán trong suốt</option>
          </SelectField>

          <div className="subtitle-removal-summary">
            <div>
              <strong>Vùng xử lý trên preview</strong>
              <span>
                X {Math.round(subtitleRemoval.x * 100)}% · Y {Math.round(subtitleRemoval.y * 100)}% ·{' '}
                {Math.round(subtitleRemoval.width * 100)}×{Math.round(subtitleRemoval.height * 100)}%
              </span>
            </div>
            <button
              type="button"
              disabled={jobBusy}
              onClick={() => onSubtitleRemovalChange({
                ...subtitleRemoval,
                x: 0.05,
                y: 0.70,
                width: 0.90,
                height: 0.16,
                regions: [{
                  id: 'primary',
                  x: 0.05,
                  y: 0.70,
                  width: 0.90,
                  height: 0.16,
                }],
              })}
            >
              Đặt lại
            </button>
          </div>

          <div className="subtitle-removal-regions">
            <div className="subtitle-removal-regions__heading">
              <div>
                <strong>{removalRegions.length} vùng che</strong>
                <span>Tối đa {maxSubtitleRemovalRegions} vùng trên một video</span>
              </div>
              <button
                type="button"
                className="subtitle-removal-add"
                disabled={jobBusy || removalRegions.length >= maxSubtitleRemovalRegions}
                onClick={addRemovalRegion}
              >
                <Plus size={14} />
                Thêm vùng che
              </button>
            </div>
            <div className="subtitle-removal-region-list">
              {removalRegions.map((region, index) => (
                <div className="subtitle-removal-region-item" key={region.id}>
                  <span className="subtitle-removal-region-index">{index + 1}</span>
                  <span className="subtitle-removal-region-size">
                    X {Math.round(region.x * 100)} · Y {Math.round(region.y * 100)} ·{' '}
                    {Math.round(region.width * 100)}×{Math.round(region.height * 100)}%
                  </span>
                  <button
                    type="button"
                    aria-label={`Xóa vùng che ${index + 1}`}
                    title={removalRegions.length <= 1 ? 'Cần giữ lại ít nhất một vùng' : `Xóa vùng che ${index + 1}`}
                    disabled={jobBusy || removalRegions.length <= 1}
                    onClick={() => removeRemovalRegion(region.id)}
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="panel-tip panel-tip--removal">
            <ScanLine size={16} />
            <div>
              <strong>Kéo khung xanh ngay trên video</strong>
              <p>Đặt khung phủ hết chữ Trung và phần viền đen, không cần bao phủ phụ đề Việt.</p>
            </div>
          </div>
        </SectionCard>

        <div className="panel-tip">
          <Sparkles size={16} />
          <div>
            <strong>Mẹo chất lượng</strong>
            <p>Duyệt transcript trước khi tạo giọng để hạn chế sai tên riêng.</p>
          </div>
        </div>
      </div>

      <footer className="panel-footer processing-footer">
        {modelDownloadPercent !== null ? (
          <div className="processing-footer__status" aria-live="polite">
            <LoaderCircle className="spin" size={17} />
            <div>
              <strong>Đang chuẩn bị AI local</strong>
              <span>{Math.round(modelDownloadPercent)}% · tải và kiểm tra gói local</span>
            </div>
          </div>
        ) : displayJob ? (
          <div className="processing-footer__status" aria-live="polite">
            <span className={`job-state-dot job-state-dot--${displayJob.status}`} />
            <div>
              <strong>{jobTitle()}</strong>
              <span title={displayJob.errorMessage ?? undefined}>
                {displayJob.status === 'failed' && displayJob.errorCode
                  ? `${displayJob.errorCode} · ${displayJob.errorMessage ?? 'Không có mô tả lỗi'}`
                  : `${Math.round(displayJob.progressPercent)}% · ${displayJob.currentStep ?? displayJob.jobType}`}
              </span>
              {displayJob.translationMetrics && displayJob.jobType === 'TRANSLATE_CLOUD' ? (
                <span>
                  {formatTokenCount(displayJob.translationMetrics.inputTokens + displayJob.translationMetrics.outputTokens)} token
                  {' · '}{displayJob.translationMetrics.apiRequests} request
                  {displayJob.translationMetrics.cacheHitScenes > 0
                    ? ` · ${displayJob.translationMetrics.cacheHitScenes} cache hit`
                    : ''}
                  {displayJob.translationMetrics.autoRepairedCues > 0
                    ? ` · sửa ${displayJob.translationMetrics.autoRepairedCues} cue`
                    : ''}
                  {displayJob.translationMetrics.skippedCues > 0
                    ? ` · chú ý ${displayJob.translationMetrics.skippedCues} cue`
                    : ''}
                </span>
              ) : null}
              {displayJob.voiceMetrics && displayJob.jobType === 'SYNTHESIZE_VOICE_CLOUD' ? (
                <span>
                  {formatTokenCount(displayJob.voiceMetrics.submittedCharacters)} ký tự đã gửi
                  {' · '}{displayJob.voiceMetrics.apiRequests} request
                  {' · '}{displayJob.voiceMetrics.completedCues}/{displayJob.voiceMetrics.totalCues} cue
                  {displayJob.voiceMetrics.cacheHitCues > 0
                    ? ` · ${displayJob.voiceMetrics.cacheHitCues} cache hit`
                    : ''}
                </span>
              ) : null}
            </div>
          </div>
        ) : (
          <div className="processing-footer__status">
            <span className="job-state-dot" />
            <div><strong>Chưa nhận dạng giọng nói</strong><span>Model chỉ tải một lần và chạy local</span></div>
          </div>
        )}

        <div className="processing-footer__actions">
          {canStart ? (
            <button
              type="button"
              className="prepare-audio-button"
              disabled={!canPrepareAudio || jobBusy}
              onClick={onTranscribe}
            >
              {jobBusy ? <LoaderCircle className="spin" size={15} /> : <AudioWaveform size={15} />}
              Nhận dạng
            </button>
          ) : null}
          {canStart ? (
            <button
              type="button"
              title="Nhận dạng phụ đề cứng bằng PaddleOCR local"
              aria-label="Nhận dạng phụ đề cứng"
              disabled={!canRunOcr || jobBusy}
              onClick={onOcr}
            >
              <ScanLine size={15} />
            </button>
          ) : null}
          {!audioJob || terminalStates.includes(audioJob.status) ? (
            <button
              type="button"
              title="Chỉ chuẩn hóa audio WAV 16 kHz"
              aria-label="Chuẩn hóa audio nguồn"
              disabled={!canPrepareAudio || jobBusy}
              onClick={onPrepareAudio}
            >
              {audioJob?.status === 'completed' ? <CheckCircle2 size={15} /> : <Play size={15} />}
            </button>
          ) : null}
          {displayJob?.status === 'running' ? (
            <>
              <button type="button" title="Tạm dừng" aria-label="Tạm dừng công việc" disabled={jobBusy} onClick={() => onPauseJob(displayJob.jobId)}><Pause size={15} /></button>
              <button type="button" title="Hủy" aria-label="Hủy công việc" disabled={jobBusy} onClick={() => onCancelJob(displayJob.jobId)}><Square size={14} /></button>
            </>
          ) : null}
          {displayJob && ['paused', 'interrupted'].includes(displayJob.status) ? (
            <>
              <button type="button" className="prepare-audio-button" disabled={jobBusy} onClick={() => onResumeJob(displayJob.jobId)}><Play size={15} /> Tiếp tục</button>
              <button type="button" title="Hủy" aria-label="Hủy công việc" disabled={jobBusy} onClick={() => onCancelJob(displayJob.jobId)}><Square size={14} /></button>
            </>
          ) : null}
          {displayJob?.status === 'failed' ? (
            <button type="button" className="prepare-audio-button" disabled={jobBusy} onClick={() => onRetryJob(displayJob.jobId)}><RotateCcw size={15} /> Thử lại</button>
          ) : null}
        </div>
      </footer>
    </aside>
  )
}
