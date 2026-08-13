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
  RotateCcw,
  ScanLine,
  Sparkles,
  Square,
  Type,
} from 'lucide-react'
import type { LocalJobInfo, SubtitleRemovalSettings, SubtitleStyleSettings } from '../types'
import { SubtitleStyleEditor } from './SubtitleStyleEditor'
import { SectionCard, SegmentTab, SelectField, Toggle } from './Ui'

type SettingsPanelProps = {
  sourceLanguageCode: string
  ocrLanguageCode: string
  translationModelId: string
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

export function SettingsPanel({
  sourceLanguageCode,
  ocrLanguageCode,
  translationModelId,
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

  const jobTitle = () => {
    if (!displayJob) return 'Chưa nhận dạng giọng nói'
    const jobName: Record<string, string> = {
      EXTRACT_AUDIO: 'Chuẩn hóa audio',
      TRANSCRIBE_LOCAL: 'Nhận dạng giọng nói',
      OCR_LOCAL: 'Nhận dạng phụ đề cứng',
      TRANSLATE_LOCAL: 'Dịch sang tiếng Việt',
      SYNTHESIZE_VOICE_LOCAL: 'Tạo giọng Việt',
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
          <SelectField
            label="MÔ HÌNH DỊCH"
            value={translationModelId}
            disabled
            helper="Xử lý offline, không gửi nội dung phụ đề lên cloud"
          >
            <option value="auto">Tự chọn theo ngôn ngữ gốc</option>
            <option value="opus-mt-zh-vi-official-v2">OPUS-MT Chinese → Vietnamese · Official</option>
            <option value="argos-en-vi">Argos English → Vietnamese</option>
          </SelectField>

          <div className="panel-tip">
            <Sparkles size={16} />
            <div>
              <strong>Dịch trực tiếp Trung → Việt</strong>
              <p>Model OPUS-MT chính thức chạy local; không dịch vòng qua tiếng Anh.</p>
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
              })}
            >
              Đặt lại
            </button>
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
              <span>{Math.round(displayJob.progressPercent)}% · {displayJob.currentStep ?? 'EXTRACT_AUDIO'}</span>
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
