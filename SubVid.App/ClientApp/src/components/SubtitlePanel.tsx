import { useEffect, useMemo, useState } from 'react'
import {
  AudioLines,
  Captions,
  CircleAlert,
  Download,
  FileUp,
  ListFilter,
  MessageSquareText,
  MoreHorizontal,
  Search,
  SlidersHorizontal,
  Sparkles,
  WandSparkles,
} from 'lucide-react'
import { formatClock } from '../lib/format'
import type { SubtitleSegment, VoiceInfo } from '../types'
import { IconButton, SegmentTab } from './Ui'

type Filter = 'all' | 'untranslated' | 'review' | 'missing-audio' | 'invalid-translation'

type SubtitlePanelProps = {
  segments: SubtitleSegment[]
  selectedId: number | null
  onSelect: (id: number) => void
  busy: boolean
  onImportSrt: () => void
  onExportSrt: () => void
  onTranslate: () => void
  onSynthesizeVoice: () => void
  onUpdateSegment: (cueId: string, original: string, translated: string) => void
  voices: VoiceInfo[]
  onUpdateVoice: (cueId: string, speaker: string, voiceId: string | null) => void
}

const statusLabels: Record<SubtitleSegment['status'], string> = {
  translated: 'Đã dịch',
  review: 'Cần chú ý',
  'missing-audio': 'Thiếu audio',
  'invalid-translation': 'Lỗi bản dịch',
}

export function SubtitlePanel({
  segments,
  selectedId,
  onSelect,
  busy,
  onImportSrt,
  onExportSrt,
  onTranslate,
  onSynthesizeVoice,
  onUpdateSegment,
  voices,
  onUpdateVoice,
}: SubtitlePanelProps) {
  const [tab, setTab] = useState<'subtitles' | 'properties'>('subtitles')
  const [filter, setFilter] = useState<Filter>('all')
  const [query, setQuery] = useState('')
  const [draftOriginal, setDraftOriginal] = useState('')
  const [draftTranslated, setDraftTranslated] = useState('')
  const [draftSpeaker, setDraftSpeaker] = useState('speaker_1')
  const [draftVoiceId, setDraftVoiceId] = useState('')

  const visibleSegments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase('vi')
    return segments.filter((segment) => {
      const matchesFilter =
        filter === 'all' ||
        (filter === 'untranslated'
          ? segment.translated.trim().length === 0
          : segment.status === filter)
      const matchesQuery =
        !normalizedQuery ||
        segment.original.toLocaleLowerCase('vi').includes(normalizedQuery) ||
        segment.translated.toLocaleLowerCase('vi').includes(normalizedQuery)
      return matchesFilter && matchesQuery
    })
  }, [segments, filter, query])

  const selectedSegment = segments.find((segment) => segment.id === selectedId)
  const invalidTranslationCount = segments.filter(
    (segment) => segment.status === 'invalid-translation',
  ).length
  const translatedCount = segments.filter(
    (segment) => segment.translated.trim().length > 0,
  ).length

  useEffect(() => {
    setDraftOriginal(selectedSegment?.original ?? '')
    setDraftTranslated(selectedSegment?.translated ?? '')
    setDraftSpeaker(selectedSegment?.speaker ?? 'speaker_1')
    setDraftVoiceId(selectedSegment?.voiceId ?? '')
  }, [
    selectedSegment?.cueId,
    selectedSegment?.original,
    selectedSegment?.translated,
    selectedSegment?.speaker,
    selectedSegment?.voiceId,
  ])

  return (
    <aside className="panel subtitle-panel" aria-label="Danh sách phụ đề">
      <div className="panel-mode-tabs panel-mode-tabs--right" role="tablist">
        <SegmentTab
          active={tab === 'subtitles'}
          icon={<Captions size={16} />}
          label="Phụ đề"
          onClick={() => setTab('subtitles')}
        />
        <SegmentTab
          active={tab === 'properties'}
          icon={<SlidersHorizontal size={16} />}
          label="Thuộc tính"
          onClick={() => setTab('properties')}
        />
      </div>

      {tab === 'subtitles' ? (
        <div className="subtitle-panel__body">
          <div className="subtitle-heading">
            <div>
              <span className="eyebrow">DANH SÁCH PHỤ ĐỀ</span>
              <small>{segments.length} phân đoạn</small>
            </div>
          </div>

          <div className="search-row">
            <label className="search-box">
              <Search size={15} />
              <input
                type="search"
                value={query}
                placeholder="Tìm trong phụ đề..."
                aria-label="Tìm trong phụ đề"
                onChange={(event) => setQuery(event.target.value)}
              />
            </label>
            <IconButton label="Tùy chọn tìm kiếm" size="small">
              <ListFilter size={15} />
            </IconButton>
          </div>

          <div
            className="filter-tabs"
            role="group"
            aria-label="Lọc phụ đề"
            onWheel={(event) => {
              const tabs = event.currentTarget
              const maximumScroll = tabs.scrollWidth - tabs.clientWidth
              if (maximumScroll <= 0 || Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return
              const nextScroll = Math.max(0, Math.min(maximumScroll, tabs.scrollLeft + event.deltaY))
              if (nextScroll === tabs.scrollLeft) return
              tabs.scrollLeft = nextScroll
              event.preventDefault()
            }}
          >
            {[
              { id: 'all' as const, label: 'Tất cả' },
              { id: 'untranslated' as const, label: 'Chưa dịch' },
              { id: 'review' as const, label: 'Cần chú ý' },
              { id: 'invalid-translation' as const, label: 'Lỗi dịch' },
              { id: 'missing-audio' as const, label: 'Thiếu audio' },
            ].map((item) => (
              <button
                key={item.id}
                type="button"
                className={filter === item.id ? 'is-active' : ''}
                onClick={() => setFilter(item.id)}
              >
                {item.label}
              </button>
            ))}
          </div>

          <div className="subtitle-actions">
            <button
              type="button"
              className={invalidTranslationCount > 0 ? 'is-warning' : undefined}
              onClick={onTranslate}
              disabled={busy || segments.length === 0}
            >
              <WandSparkles size={15} />
              <span>{invalidTranslationCount > 0 ? `Dịch lại ${invalidTranslationCount} lỗi` : 'Dịch'}</span>
            </button>
            <button
              type="button"
              onClick={onSynthesizeVoice}
              disabled={busy || translatedCount === 0 || invalidTranslationCount > 0}
              title={invalidTranslationCount > 0
                ? 'Cần dịch lại các phân đoạn lỗi trước khi tạo giọng.'
                : undefined}
            >
              <AudioLines size={15} />
              <span>Tạo giọng</span>
            </button>
            <button
              type="button"
              className="is-accent"
              onClick={onImportSrt}
              disabled={busy}
            >
              <FileUp size={15} />
              <span>Nhập SRT</span>
            </button>
            <button
              type="button"
              onClick={onExportSrt}
              disabled={busy || segments.length === 0}
            >
              <Download size={15} />
              <span>Xuất SRT</span>
            </button>
          </div>

          <div className="subtitle-list" aria-live="polite">
            {segments.length === 0 ? (
              <div className="empty-subtitles">
                <span><MessageSquareText size={26} /></span>
                <strong>Chưa có bản chép lời</strong>
                <p>Nhập video và chọn “Nhận dạng” để bắt đầu.</p>
              </div>
            ) : visibleSegments.length === 0 ? (
              <div className="empty-subtitles empty-subtitles--compact">
                <span><Search size={22} /></span>
                <strong>Không tìm thấy kết quả</strong>
                <p>Thử thay đổi từ khóa hoặc bộ lọc.</p>
              </div>
            ) : (
              visibleSegments.map((segment) => (
                <button
                  type="button"
                  key={segment.id}
                  className={`subtitle-card ${selectedId === segment.id ? 'is-selected' : ''}`}
                  onClick={() => onSelect(segment.id)}
                >
                  <span className="subtitle-card__index">{String(segment.id).padStart(2, '0')}</span>
                  <span className="subtitle-card__content">
                    <span className="subtitle-card__meta">
                      <time>{formatClock(segment.start)} — {formatClock(segment.end)}</time>
                      <span
                        className={`status-badge status-badge--${segment.status}`}
                        title={segment.translationWarnings?.length
                          ? segment.translationWarnings.join(', ')
                          : undefined}
                      >
                        {segment.status !== 'translated' ? <CircleAlert size={11} /> : null}
                        {statusLabels[segment.status]}
                      </span>
                    </span>
                    <span className="subtitle-original">{segment.original}</span>
                    <span className="subtitle-translated">{segment.translated}</span>
                  </span>
                  <MoreHorizontal size={16} className="subtitle-card__more" />
                </button>
              ))
            )}
          </div>
        </div>
      ) : (
        <div className="properties-panel">
          {selectedSegment ? (
            <>
              <div className="properties-title">
                <span className="properties-icon"><Sparkles size={17} /></span>
                <div>
                  <strong>Phân đoạn {String(selectedSegment.id).padStart(2, '0')}</strong>
                  <small>{formatClock(selectedSegment.start)} — {formatClock(selectedSegment.end)}</small>
                </div>
              </div>
              <label>
                <span>Nội dung gốc</span>
                <textarea
                  value={draftOriginal}
                  rows={3}
                  disabled={busy}
                  onChange={(event) => setDraftOriginal(event.target.value)}
                />
              </label>
              <label>
                <span>Bản dịch tiếng Việt</span>
                <textarea
                  value={draftTranslated}
                  rows={4}
                  disabled={busy}
                  onChange={(event) => setDraftTranslated(event.target.value)}
                />
              </label>
              <label>
                <span>Nhân vật / speaker</span>
                <input
                  type="text"
                  value={draftSpeaker}
                  maxLength={80}
                  disabled={busy}
                  onChange={(event) => setDraftSpeaker(event.target.value)}
                />
              </label>
              <label>
                <span>Giọng đọc riêng</span>
                <select
                  value={draftVoiceId}
                  disabled={busy || voices.length === 0}
                  onChange={(event) => setDraftVoiceId(event.target.value)}
                >
                  <option value="">Theo phân vai / mặc định</option>
                  {voices.map((voice) => (
                    <option key={voice.voiceId} value={voice.voiceId}>
                      {voice.displayName} · {voice.gender} · {voice.region}
                    </option>
                  ))}
                </select>
                {selectedSegment.resolvedVoiceId ? (
                  <small>Đang áp dụng: {voices.find((voice) => (
                    voice.voiceId === selectedSegment.resolvedVoiceId
                  ))?.displayName ?? selectedSegment.resolvedVoiceId}</small>
                ) : null}
              </label>
              <button
                type="button"
                className="save-segment-button save-segment-button--secondary"
                disabled={busy || !draftSpeaker.trim()}
                onClick={() => onUpdateVoice(
                  selectedSegment.cueId,
                  draftSpeaker,
                  draftVoiceId || null,
                )}
              >
                Lưu nhân vật &amp; giọng
              </button>
              <button
                type="button"
                className="save-segment-button"
                disabled={busy || !draftOriginal.trim()}
                onClick={() => onUpdateSegment(
                  selectedSegment.cueId,
                  draftOriginal,
                  draftTranslated,
                )}
              >
                {busy ? 'Đang lưu…' : 'Lưu thay đổi'}
              </button>
            </>
          ) : (
            <div className="empty-subtitles">
              <span><SlidersHorizontal size={25} /></span>
              <strong>Chưa chọn phân đoạn</strong>
              <p>Chọn một câu phụ đề để chỉnh sửa thuộc tính.</p>
            </div>
          )}
        </div>
      )}
    </aside>
  )
}
