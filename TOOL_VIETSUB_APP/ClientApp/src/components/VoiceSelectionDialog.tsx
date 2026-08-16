import { useEffect, useMemo, useRef, useState } from 'react'
import {
  AudioLines,
  CheckCircle2,
  Cloud,
  Download,
  Gauge,
  KeyRound,
  MapPin,
  Play,
  ShieldCheck,
  UserRound,
  X,
} from 'lucide-react'
import type { VoiceSettingsInfo } from '../types'

type VoiceFilter = 'all' | 'local' | 'fpt'

type VoiceSelectionDialogProps = {
  open: boolean
  settings: VoiceSettingsInfo | null
  busy: boolean
  previewBusy: boolean
  previewAudioDataUrl: string | null
  downloadPercent: number | null
  onClose: () => void
  onInstall: (voiceId: string) => void
  onPreview: (voiceId: string, speed: number, apiKey?: string) => void
  onConfirm: (voiceId: string, speed: number, apiKey?: string) => void
}

const formatCharacters = (value: number) => new Intl.NumberFormat('vi-VN').format(Math.max(0, value))

export function VoiceSelectionDialog({
  open,
  settings,
  busy,
  previewBusy,
  previewAudioDataUrl,
  downloadPercent,
  onClose,
  onInstall,
  onPreview,
  onConfirm,
}: VoiceSelectionDialogProps) {
  const [selectedVoiceId, setSelectedVoiceId] = useState('')
  const [speed, setSpeed] = useState(0)
  const [apiKey, setApiKey] = useState('')
  const [filter, setFilter] = useState<VoiceFilter>('all')
  const confirmButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!open || !settings) return
    setSelectedVoiceId(settings.defaultVoiceId || settings.voices[0]?.voiceId || '')
    setSpeed(settings.speed ?? 0)
    setApiKey('')
    setFilter('all')
  }, [open, settings?.defaultVoiceId, settings?.speed])

  useEffect(() => {
    if (!open) return
    const previousFocus = document.activeElement as HTMLElement | null
    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy && !previewBusy) onClose()
    }
    window.addEventListener('keydown', keydown)
    window.setTimeout(() => confirmButtonRef.current?.focus(), 0)
    return () => {
      window.removeEventListener('keydown', keydown)
      previousFocus?.focus()
    }
  }, [busy, onClose, open, previewBusy])

  const selectedVoice = useMemo(
    () => settings?.voices.find((voice) => voice.voiceId === selectedVoiceId) ?? null,
    [selectedVoiceId, settings],
  )
  const visibleVoices = useMemo(() => settings?.voices.filter((voice) => (
    filter === 'all' || (filter === 'fpt' ? voice.isCloud : !voice.isCloud)
  )) ?? [], [filter, settings])
  const hasFptCredential = Boolean(settings?.fptApiKeyConfigured || apiKey.trim())
  const actionBusy = busy || previewBusy

  if (!open || !settings) return null

  return (
    <div
      className="import-dialog-backdrop voice-selection-dialog-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (!actionBusy && event.target === event.currentTarget) onClose()
      }}
    >
      <div
        className="import-dialog voice-selection-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="voice-selection-dialog-title"
        aria-describedby="voice-selection-dialog-description"
      >
        <header>
          <div>
            <span><AudioLines size={15} /> Tạo giọng tiếng Việt</span>
            <h2 id="voice-selection-dialog-title">Chọn giọng đọc cho dự án</h2>
            <p id="voice-selection-dialog-description">
              Dùng giọng local miễn phí trên máy hoặc FPT.AI online với API key của bạn.
            </p>
          </div>
          <button type="button" disabled={actionBusy} aria-label="Đóng" onClick={onClose}>
            <X size={18} />
          </button>
        </header>

        {downloadPercent !== null ? (
          <div className="voice-selection-download" aria-live="polite">
            <div>
              <strong>Đang chuẩn bị model giọng đọc</strong>
              <span>{Math.round(downloadPercent)}%</span>
            </div>
            <div><span style={{ width: `${Math.max(0, Math.min(100, downloadPercent))}%` }} /></div>
          </div>
        ) : null}

        <div className="voice-selection-filters" role="group" aria-label="Lọc nhà cung cấp giọng đọc">
          {([
            ['all', 'Tất cả'],
            ['local', 'Local'],
            ['fpt', 'FPT.AI online'],
          ] as const).map(([value, label]) => (
            <button
              type="button"
              key={value}
              className={filter === value ? 'is-active' : ''}
              disabled={actionBusy}
              onClick={() => setFilter(value)}
            >
              {value === 'fpt' ? <Cloud size={13} /> : null}{label}
            </button>
          ))}
        </div>

        <div className="voice-selection-grid" role="radiogroup" aria-label="Danh sách giọng đọc">
          {visibleVoices.map((voice) => {
            const selected = voice.voiceId === selectedVoiceId
            return (
              <article
                key={voice.voiceId}
                className={`voice-selection-card ${selected ? 'is-selected' : ''} ${voice.isCloud ? 'is-cloud' : ''}`}
              >
                <label>
                  <input
                    type="radio"
                    name="project-voice"
                    value={voice.voiceId}
                    checked={selected}
                    disabled={actionBusy}
                    onChange={() => setSelectedVoiceId(voice.voiceId)}
                  />
                  <span className={`voice-engine voice-engine--${voice.engine}`}>{voice.isCloud ? 'FPT.AI' : voice.engine}</span>
                  <span className={voice.isCloud || voice.installed ? 'voice-ready is-ready' : 'voice-ready'}>
                    {voice.isCloud
                      ? <Cloud size={13} />
                      : voice.installed ? <CheckCircle2 size={13} /> : <Download size={13} />}
                    {voice.isCloud ? 'Online' : voice.installed ? 'Đã cài' : 'Chưa cài'}
                  </span>
                  <strong>{voice.displayName}</strong>
                  <span className="voice-selection-card__meta">
                    <span><UserRound size={12} /> {voice.gender}</span>
                    <span><MapPin size={12} /> Miền {voice.region}</span>
                  </span>
                  <small>{voice.style} · {voice.modelVersion} · {voice.license}</small>
                </label>
                {voice.requiresInstall && !voice.installed ? (
                  <button type="button" disabled={actionBusy} onClick={() => onInstall(voice.voiceId)}>
                    <Download size={13} /> Cài model
                  </button>
                ) : null}
              </article>
            )
          })}
        </div>

        {selectedVoice?.isCloud ? (
          <section className="voice-cloud-controls">
            <div className="voice-cloud-controls__row">
              <label className="voice-cloud-key">
                <span><KeyRound size={14} /> API key FPT.AI</span>
                <input
                  type="password"
                  autoComplete="off"
                  value={apiKey}
                  disabled={actionBusy}
                  placeholder={settings.fptApiKeyConfigured ? 'Đã lưu an toàn · nhập để thay thế' : 'Nhập API key từ console.fpt.ai'}
                  onChange={(event) => setApiKey(event.target.value)}
                />
                <small>
                  {settings.fptApiKeyConfigured
                    ? 'Đã có API key mã hóa theo tài khoản Windows.'
                    : 'Key chỉ được mã hóa trên máy, không lưu trong project.'}
                </small>
              </label>
              <label className="voice-speed-control">
                <span><Gauge size={14} /> Tốc độ: {speed > 0 ? `+${speed}` : speed}</span>
                <input
                  type="range"
                  min={-3}
                  max={3}
                  step={1}
                  value={speed}
                  disabled={actionBusy}
                  onChange={(event) => setSpeed(Number(event.target.value))}
                />
                <small>-3 chậm · 0 bình thường · +3 nhanh</small>
              </label>
            </div>
            <div className="voice-cloud-preview">
              <button
                type="button"
                disabled={actionBusy || !hasFptCredential}
                onClick={() => onPreview(selectedVoice.voiceId, speed, apiKey.trim() || undefined)}
              >
                <Play size={14} /> {previewBusy ? 'Đang tạo bản nghe thử…' : 'Nghe thử & kiểm tra key'}
              </button>
              {previewAudioDataUrl ? <audio controls autoPlay src={previewAudioDataUrl} /> : null}
            </div>
          </section>
        ) : null}

        <footer className="voice-selection-dialog__footer">
          <p>
            <ShieldCheck size={14} />
            {selectedVoice?.isCloud
              ? `Ước tính ${formatCharacters(settings.estimatedCharacters)} ký tự; chỉ cue chưa có audio mới gửi lên FPT.AI.`
              : 'Audio đã tạo đúng nội dung và đúng giọng sẽ được tái sử dụng.'}
          </p>
          <div>
            <button type="button" className="secondary" disabled={actionBusy} onClick={onClose}>Hủy</button>
            <button
              ref={confirmButtonRef}
              type="button"
              className="primary"
              disabled={actionBusy || !selectedVoice || (selectedVoice.isCloud && !hasFptCredential)}
              onClick={() => selectedVoice && onConfirm(
                selectedVoice.voiceId,
                selectedVoice.isCloud ? speed : 0,
                apiKey.trim() || undefined,
              )}
            >
              <AudioLines size={15} />
              {selectedVoice?.isCloud
                ? 'Tạo giọng bằng FPT.AI'
                : selectedVoice?.installed ? 'Tạo giọng local' : 'Cài và tạo giọng'}
            </button>
          </div>
        </footer>
      </div>
    </div>
  )
}
