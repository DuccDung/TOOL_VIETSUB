import { useEffect, useMemo, useState } from 'react'
import { AudioLines, CheckCircle2, Cloud, Download, Gauge, HardDrive, KeyRound, Library, RefreshCw, Trash2, Users } from 'lucide-react'
import type { AiStorageInfo, SubtitleSegment, VoiceSettingsInfo } from '../types'

type VoiceWorkspaceProps = {
  mode: 'voice' | 'library'
  settings: VoiceSettingsInfo | null
  segments: SubtitleSegment[]
  busy: boolean
  downloadPercent: number | null
  storage: AiStorageInfo | null
  onSave: (defaultVoiceId: string, speakerVoiceIds: Record<string, string>, speed: number) => void
  onSaveFptCredential: (apiKey?: string, clearApiKey?: boolean) => void
  onInstall: (voiceId: string) => void
  onSynthesize: () => void
  onSelectStorage: () => void
  onResumeStorage: (destinationPath: string) => void
  onDiscardPendingStorage: () => void
}

export function VoiceWorkspace({
  mode,
  settings,
  segments,
  busy,
  downloadPercent,
  storage,
  onSave,
  onSaveFptCredential,
  onInstall,
  onSynthesize,
  onSelectStorage,
  onResumeStorage,
  onDiscardPendingStorage,
}: VoiceWorkspaceProps) {
  const [defaultVoiceId, setDefaultVoiceId] = useState('')
  const [speakerVoiceIds, setSpeakerVoiceIds] = useState<Record<string, string>>({})
  const [speed, setSpeed] = useState(0)
  const [fptApiKey, setFptApiKey] = useState('')

  useEffect(() => {
    setDefaultVoiceId(settings?.defaultVoiceId ?? '')
    setSpeakerVoiceIds(settings?.speakerVoiceIds ?? {})
    setSpeed(settings?.speed ?? 0)
    setFptApiKey('')
  }, [settings])

  const speakers = useMemo(() => Array.from(new Set(
    segments.map((segment) => segment.speaker?.trim()).filter((value): value is string => Boolean(value)),
  )).sort((left, right) => left.localeCompare(right, 'vi')), [segments])

  if (!settings) {
    return (
      <main id="editor-workspace" className="voice-workspace voice-workspace--empty">
        <AudioLines size={34} />
        <strong>Chưa có dự án đang mở</strong>
        <p>Hãy tạo hoặc mở một dự án để cấu hình giọng đọc local hoặc FPT.AI.</p>
      </main>
    )
  }

  const selectedVoice = settings.voices.find((voice) => voice.voiceId === defaultVoiceId)

  return (
    <main id="editor-workspace" className="voice-workspace">
      <header className="voice-workspace__header">
        <div>
          <span className="eyebrow">{mode === 'library' ? 'KHO GIỌNG VIỆT' : 'GIỌNG ĐỌC DỰ ÁN'}</span>
          <h1>{mode === 'library' ? 'Các giọng tiếng Việt' : 'Phân vai giọng đọc'}</h1>
          <p>
            {mode === 'library'
              ? 'Dùng model local trên máy hoặc 7 giọng Bắc–Trung–Nam của FPT.AI online.'
              : 'Ưu tiên áp dụng: giọng riêng của câu → giọng nhân vật → giọng mặc định.'}
          </p>
        </div>
        {mode === 'voice' ? (
          <button
            type="button"
            className="voice-primary-action"
            disabled={busy || segments.every((segment) => !segment.translated.trim())}
            onClick={onSynthesize}
          >
            <AudioLines size={17} /> Tạo giọng còn thiếu
          </button>
        ) : null}
      </header>

      {storage ? (
        <section className="voice-storage-panel">
          <div className="voice-section-title">
            <HardDrive size={18} />
            <div>
              <strong>Vị trí lưu AI local</strong>
              <small>Runtime, model và cache được lưu tại đây.</small>
            </div>
          </div>
          <div className="voice-storage-panel__content">
            <div className="voice-storage-panel__details">
              <code title={storage.rootPath}>{storage.rootPath}</code>
              <span>{(storage.freeBytes / (1024 ** 3)).toFixed(1)} GB trống</span>
              {storage.usesLegacyLocation ? <em>Nên chuyển khỏi ổ hệ thống</em> : null}
            </div>
            {storage.pendingMigrationPath ? (
              <p className="voice-storage-panel__pending" title={storage.pendingMigrationPath}>
                Migration sang <code>{storage.pendingMigrationPath}</code> còn dang dở.
              </p>
            ) : null}
          </div>
          <div className="voice-storage-panel__actions">
            {storage.pendingMigrationPath ? (
              <>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => onResumeStorage(storage.pendingMigrationPath!)}
                >
                  <RefreshCw size={13} /> Tiếp tục
                </button>
                <button
                  type="button"
                  className="is-secondary"
                  disabled={busy}
                  onClick={onDiscardPendingStorage}
                >
                  <Trash2 size={13} /> Bỏ bản tạm
                </button>
              </>
            ) : (
              <button type="button" disabled={busy} onClick={onSelectStorage}>
                Chọn thư mục lưu AI
              </button>
            )}
          </div>
        </section>
      ) : null}

      {mode === 'voice' ? (
        <section className="voice-assignment-panel">
          <div className="voice-section-title">
            <HardDrive size={18} />
            <div><strong>Giọng mặc định</strong><small>Dùng cho các câu chưa được phân vai.</small></div>
          </div>
          <label className="voice-field">
            <span>Giọng của dự án</span>
            <select
              value={defaultVoiceId}
              disabled={busy}
              onChange={(event) => setDefaultVoiceId(event.target.value)}
            >
              {settings.voices.map((voice) => (
                <option key={voice.voiceId} value={voice.voiceId}>
                  {voice.displayName} · {voice.gender} · {voice.region} · {voice.style}
                </option>
              ))}
            </select>
            {selectedVoice?.requiresInstall && !selectedVoice.installed ? (
              <small className="voice-install-note">Giọng này sẽ cần cài model trước lần tạo đầu tiên.</small>
            ) : null}
            {selectedVoice?.isCloud ? (
              <small className="voice-install-note">Giọng online dùng API key FPT.AI và không tải model về máy.</small>
            ) : null}
          </label>

          {selectedVoice?.isCloud ? (
            <div className="voice-workspace-cloud-settings">
              <label className="voice-field">
                <span><KeyRound size={14} /> API key FPT.AI</span>
                <input
                  type="password"
                  autoComplete="off"
                  value={fptApiKey}
                  disabled={busy}
                  placeholder={settings.fptApiKeyConfigured ? 'Đã lưu an toàn · nhập để thay thế' : 'Nhập key từ console.fpt.ai'}
                  onChange={(event) => setFptApiKey(event.target.value)}
                />
                <small>Key được mã hóa theo tài khoản Windows, không ghi vào project.</small>
              </label>
              <label className="voice-field voice-field--speed">
                <span><Gauge size={14} /> Tốc độ FPT.AI: {speed > 0 ? `+${speed}` : speed}</span>
                <input
                  type="range"
                  min={-3}
                  max={3}
                  step={1}
                  value={speed}
                  disabled={busy}
                  onChange={(event) => setSpeed(Number(event.target.value))}
                />
              </label>
              <div className="voice-workspace-cloud-settings__actions">
                <button
                  type="button"
                  disabled={busy || !fptApiKey.trim()}
                  onClick={() => onSaveFptCredential(fptApiKey.trim())}
                >
                  Lưu API key
                </button>
                {settings.fptApiKeyConfigured ? (
                  <button type="button" className="is-secondary" disabled={busy} onClick={() => onSaveFptCredential(undefined, true)}>
                    <Trash2 size={13} /> Xóa key đã lưu
                  </button>
                ) : null}
              </div>
            </div>
          ) : null}

          <div className="voice-section-title voice-section-title--spaced">
            <Users size={18} />
            <div><strong>Giọng theo nhân vật</strong><small>Mỗi speaker có thể dùng một giọng khác nhau.</small></div>
          </div>
          {speakers.length ? (
            <div className="speaker-voice-list">
              {speakers.map((speaker) => (
                <label key={speaker} className="speaker-voice-row">
                  <span>{speaker}</span>
                  <select
                    value={speakerVoiceIds[speaker] ?? ''}
                    disabled={busy}
                    onChange={(event) => setSpeakerVoiceIds((current) => ({
                      ...current,
                      [speaker]: event.target.value,
                    }))}
                  >
                    <option value="">Theo giọng mặc định</option>
                    {settings.voices.map((voice) => (
                      <option key={voice.voiceId} value={voice.voiceId}>{voice.displayName}</option>
                    ))}
                  </select>
                </label>
              ))}
            </div>
          ) : <p className="voice-empty-note">Chưa có speaker trong danh sách phụ đề.</p>}

          <button
            type="button"
            className="voice-save-button"
            disabled={busy || !defaultVoiceId}
            onClick={() => onSave(
              defaultVoiceId,
              Object.fromEntries(Object.entries(speakerVoiceIds).filter(([, voiceId]) => voiceId)),
              speed,
            )}
          >
            Lưu phân vai
          </button>
        </section>
      ) : null}

      <section className="voice-library">
        <div className="voice-section-title">
          <Library size={18} />
          <div><strong>{mode === 'library' ? 'Tất cả giọng có sẵn' : 'Tham khảo kho giọng'}</strong><small>{settings.voices.length} preset local và online.</small></div>
        </div>
        {downloadPercent !== null ? (
          <div className="voice-download-progress" aria-live="polite">
            <span style={{ width: `${downloadPercent}%` }} />
            <small>Đang chuẩn bị model · {Math.round(downloadPercent)}%</small>
          </div>
        ) : null}
        <div className="voice-card-grid">
          {settings.voices.map((voice) => (
            <article key={voice.voiceId} className="voice-card">
              <div className="voice-card__top">
                <span className={`voice-engine voice-engine--${voice.engine}`}>{voice.engine}</span>
                <span className={voice.isCloud || voice.installed ? 'voice-ready is-ready' : 'voice-ready'}>
                  {voice.isCloud ? <Cloud size={13} /> : voice.installed ? <CheckCircle2 size={13} /> : <Download size={13} />}
                  {voice.isCloud ? 'Online' : voice.installed ? 'Đã cài' : 'Chưa cài'}
                </span>
              </div>
              <strong>{voice.displayName}</strong>
              <p>{voice.gender} · Miền {voice.region} · {voice.style}</p>
              <small>v{voice.modelVersion} · {voice.license}</small>
              <div className="voice-card__actions">
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => {
                    if (mode === 'library') {
                      onSave(
                        voice.voiceId,
                        Object.fromEntries(Object.entries(speakerVoiceIds).filter(([, voiceId]) => voiceId)),
                        speed,
                      )
                    } else {
                      setDefaultVoiceId(voice.voiceId)
                    }
                  }}
                >
                  {defaultVoiceId === voice.voiceId ? 'Đang mặc định' : 'Dùng mặc định'}
                </button>
                {voice.requiresInstall && !voice.installed ? (
                  <button
                    type="button"
                    className="is-accent"
                    disabled={busy}
                    onClick={() => onInstall(voice.voiceId)}
                  >
                    <Download size={13} /> Cài model
                  </button>
                ) : null}
              </div>
            </article>
          ))}
        </div>
      </section>
    </main>
  )
}
