import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import {
  AudioLines,
  Captions,
  CircleAlert,
  Clock3,
  Download,
  FileUp,
  Gauge,
  ListFilter,
  MessageSquareText,
  MoreHorizontal,
  Search,
  SlidersHorizontal,
  Sparkles,
  WandSparkles,
} from 'lucide-react'
import { formatClock } from '../lib/format'
import { getVirtualRowRange, updateVirtualViewport } from '../lib/longFormVirtualization'
import type { SubtitleSegment, VoiceInfo } from '../types'
import { IconButton, SegmentTab } from './Ui'

type Filter = 'all' | 'untranslated' | 'review' | 'missing-audio' | 'invalid-translation' | 'voice-timing'

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
  const subtitleBodyRef = useRef<HTMLDivElement>(null)

  const visibleSegments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase('vi')
    return segments.filter((segment) => {
      const matchesFilter =
        filter === 'all' ||
        (filter === 'untranslated'
          ? segment.translated.trim().length === 0
          : filter === 'voice-timing'
            ? segment.voiceTiming?.status === 'REVIEW_REQUIRED' || segment.voiceTiming?.status === 'INVALID'
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
  const timingAnalyzedCount = segments.filter((segment) => segment.voiceTiming).length
  const timingPaddedCount = segments.filter(
    (segment) => segment.voiceTiming?.status === 'PADDED',
  ).length
  const timingGapFittedCount = segments.filter(
    (segment) => segment.voiceTiming?.status === 'GAP_FITTED',
  ).length
  const timingTrimmedCount = segments.filter(
    (segment) => segment.voiceTiming
      && segment.voiceTiming.rawDurationSeconds - segment.voiceTiming.sourceDurationSeconds > 0.05,
  ).length
  const timingCompressedCount = segments.filter(
    (segment) => segment.voiceTiming?.status === 'COMPRESSED',
  ).length
  const timingReviewCount = segments.filter(
    (segment) => segment.voiceTiming?.status === 'REVIEW_REQUIRED' || segment.voiceTiming?.status === 'INVALID',
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
        <div ref={subtitleBodyRef} className="subtitle-panel__body">
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
              { id: 'voice-timing' as const, label: `Cảnh báo thời lượng${timingReviewCount ? ` (${timingReviewCount})` : ''}` },
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
              <span>{invalidTranslationCount > 0
                ? `Dịch lại ${invalidTranslationCount} lỗi`
                : timingReviewCount > 0
                  ? `Rút gọn ${timingReviewCount} câu`
                  : 'Dịch'}</span>
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

          {timingAnalyzedCount > 0 ? (
            <div className={`voice-timing-summary ${timingReviewCount > 0 ? 'has-errors' : ''}`}>
              <div className="voice-timing-summary__title">
                <Gauge size={14} />
                <span>
                  <strong>Kiểm tra khớp timeline</strong>
                  <small>Đã cắt im lặng {timingTrimmedCount} câu · quá thời lượng vẫn tạo giọng · tối đa 1.20x</small>
                </span>
              </div>
              <div className="voice-timing-summary__metrics">
                <span><b>{timingPaddedCount}</b> thêm khoảng lặng</span>
                <span><b>{timingGapFittedCount}</b> dùng khoảng trống</span>
                <span><b>{timingCompressedCount}</b> tăng nhẹ</span>
                <span className={timingReviewCount > 0 ? 'is-error' : ''}>
                  <b>{timingReviewCount}</b> cảnh báo cần kiểm tra
                </span>
              </div>
            </div>
          ) : null}

          <VirtualizedSubtitleList
            segments={visibleSegments}
            totalSegmentCount={segments.length}
            selectedId={selectedId}
            resetKey={`${filter}:${query}`}
            scrollHostRef={subtitleBodyRef}
            onSelect={onSelect}
          />
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
              {selectedSegment.voiceTiming ? (
                <div className={`voice-timing-detail voice-timing-detail--${selectedSegment.voiceTiming.severity.toLowerCase()}`}>
                  <div><Gauge size={15} /><strong>Khớp thời lượng giọng</strong></div>
                  <dl>
                    <div><dt>WAV gốc</dt><dd>{selectedSegment.voiceTiming.rawDurationSeconds.toFixed(2)} giây</dd></div>
                    <div><dt>Phần có giọng</dt><dd>{selectedSegment.voiceTiming.sourceDurationSeconds.toFixed(2)} giây</dd></div>
                    <div><dt>Ô phụ đề</dt><dd>{selectedSegment.voiceTiming.targetDurationSeconds.toFixed(2)} giây</dd></div>
                    <div><dt>Cửa sổ khả dụng</dt><dd>{selectedSegment.voiceTiming.effectiveWindowSeconds.toFixed(2)} giây</dd></div>
                    <div><dt>Tốc độ cần</dt><dd>{selectedSegment.voiceTiming.requiredTempo.toFixed(2)}x</dd></div>
                    <div><dt>Đã áp dụng</dt><dd>{selectedSegment.voiceTiming.appliedTempo?.toFixed(2) ?? 'Chưa áp dụng'}{selectedSegment.voiceTiming.appliedTempo ? 'x' : ''}</dd></div>
                    <div><dt>Đã cắt im lặng</dt><dd>{Math.max(0, selectedSegment.voiceTiming.rawDurationSeconds - selectedSegment.voiceTiming.sourceDurationSeconds).toFixed(2)} giây</dd></div>
                    <div><dt>Dùng khoảng trống</dt><dd>{selectedSegment.voiceTiming.borrowedGapSeconds.toFixed(2)} giây</dd></div>
                    <div><dt>Tốc độ TTS</dt><dd>{selectedSegment.voiceTiming.appliedTtsSpeed}</dd></div>
                    <div><dt>Cụm thoại</dt><dd>{selectedSegment.voiceTiming.phraseId ?? 'Riêng lẻ'}</dd></div>
                  </dl>
                  <p>{selectedSegment.voiceTiming.message}</p>
                  {selectedSegment.voiceTiming.suggestedMaximumCharacters ? (
                    <small>Nên rút còn khoảng {selectedSegment.voiceTiming.suggestedMaximumCharacters} ký tự hoặc tăng thời lượng cue.</small>
                  ) : null}
                </div>
              ) : null}
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

const subtitleRowHeight = 76

type VirtualizedSubtitleListProps = {
  segments: SubtitleSegment[]
  totalSegmentCount: number
  selectedId: number | null
  resetKey: string
  scrollHostRef: RefObject<HTMLDivElement | null>
  onSelect: (id: number) => void
}

function VirtualizedSubtitleList({
  segments,
  totalSegmentCount,
  selectedId,
  resetKey,
  scrollHostRef,
  onSelect,
}: VirtualizedSubtitleListProps) {
  const [viewport, setViewport] = useState({ scrollTop: 0, height: 600 })
  const listRef = useRef<HTMLDivElement>(null)
  const animationFrameRef = useRef<number | null>(null)
  const pendingScrollTopRef = useRef(0)
  const previousSelectedIdRef = useRef<number | null>(selectedId)

  const scheduleMeasurement = useCallback((scrollTop?: number) => {
    const list = listRef.current
    const scrollHost = scrollHostRef.current
    if (!list || !scrollHost) return
    const hostBounds = scrollHost.getBoundingClientRect()
    const listTop = list.getBoundingClientRect().top - hostBounds.top + scrollHost.scrollTop
    pendingScrollTopRef.current = scrollTop ?? Math.max(0, scrollHost.scrollTop - listTop)
    if (animationFrameRef.current !== null) return
    animationFrameRef.current = window.requestAnimationFrame(() => {
      animationFrameRef.current = null
      const currentScrollHost = scrollHostRef.current
      if (!currentScrollHost) return
      setViewport(current => updateVirtualViewport(
        current,
        pendingScrollTopRef.current,
        currentScrollHost.clientHeight,
        subtitleRowHeight,
      ))
    })
  }, [scrollHostRef])

  useEffect(() => {
    const list = listRef.current
    const scrollHost = scrollHostRef.current
    if (!list || !scrollHost) return
    const handleScroll = () => scheduleMeasurement()
    scheduleMeasurement()
    const observer = new ResizeObserver(() => scheduleMeasurement())
    observer.observe(scrollHost)
    observer.observe(list)
    scrollHost.addEventListener('scroll', handleScroll, { passive: true })
    return () => {
      observer.disconnect()
      scrollHost.removeEventListener('scroll', handleScroll)
      if (animationFrameRef.current !== null) {
        window.cancelAnimationFrame(animationFrameRef.current)
        animationFrameRef.current = null
      }
    }
  }, [scheduleMeasurement])

  useEffect(() => {
    const scrollHost = scrollHostRef.current
    if (!scrollHost) return
    scrollHost.scrollTop = 0
    scheduleMeasurement(0)
  }, [resetKey, scheduleMeasurement, scrollHostRef])

  useEffect(() => {
    const list = listRef.current
    const scrollHost = scrollHostRef.current
    const selectionChanged = previousSelectedIdRef.current !== selectedId
    previousSelectedIdRef.current = selectedId

    // Do not fight a manual scroll just because the panel re-rendered after a
    // cue update. Bringing a card into view is only appropriate for a newly
    // selected cue.
    if (!list || !scrollHost || selectedId === null || !selectionChanged) return
    const index = segments.findIndex(segment => segment.id === selectedId)
    if (index < 0) return
    const hostBounds = scrollHost.getBoundingClientRect()
    const listTop = list.getBoundingClientRect().top - hostBounds.top + scrollHost.scrollTop
    const top = listTop + index * subtitleRowHeight
    const bottom = top + subtitleRowHeight
    if (top < scrollHost.scrollTop || bottom > scrollHost.scrollTop + scrollHost.clientHeight) {
      scrollHost.scrollTop = Math.max(
        0,
        top - scrollHost.clientHeight / 2 + subtitleRowHeight / 2,
      )
      scheduleMeasurement()
    }
  }, [selectedId, segments, scheduleMeasurement, scrollHostRef])

  const virtualRange = useMemo(() => getVirtualRowRange(
    segments.length,
    viewport.scrollTop,
    viewport.height,
    subtitleRowHeight,
  ), [segments.length, viewport])
  const virtualSegments = segments.slice(virtualRange.startIndex, virtualRange.endIndex)

  return (
    <div
      ref={listRef}
      className={`subtitle-list ${segments.length > 0 ? 'is-virtualized' : ''}`}
      aria-live="polite"
    >
      {totalSegmentCount === 0 ? (
        <div className="empty-subtitles">
          <span><MessageSquareText size={26} /></span>
          <strong>Chưa có bản chép lời</strong>
          <p>Nhập video và chọn “Nhận dạng” để bắt đầu.</p>
        </div>
      ) : segments.length === 0 ? (
        <div className="empty-subtitles empty-subtitles--compact">
          <span><Search size={22} /></span>
          <strong>Không tìm thấy kết quả</strong>
          <p>Thử thay đổi từ khóa hoặc bộ lọc.</p>
        </div>
      ) : (
        <div
          className="subtitle-list__virtual-space"
          style={{ height: `${virtualRange.totalHeightPixels}px` }}
        >
          <div
            className="subtitle-list__virtual-window"
            style={{ transform: `translateY(${virtualRange.offsetPixels}px)` }}
          >
            {virtualSegments.map((segment) => (
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
                    <span className="subtitle-card__badges">
                      {segment.voiceTiming && segment.voiceTiming.status !== 'NATURAL' ? (
                        <span
                          className={`voice-timing-badge voice-timing-badge--${segment.voiceTiming.status.toLowerCase().replace('_', '-')}`}
                          title={segment.voiceTiming.message}
                        >
                          {segment.voiceTiming.status === 'PADDED' ? <Clock3 size={10} /> : <Gauge size={10} />}
                          {formatVoiceTimingBadge(segment.voiceTiming)}
                        </span>
                      ) : null}
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
                  </span>
                  <span className="subtitle-original">{segment.original}</span>
                  <span className="subtitle-translated">{segment.translated}</span>
                </span>
                <MoreHorizontal size={16} className="subtitle-card__more" />
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function formatVoiceTimingBadge(timing: NonNullable<SubtitleSegment['voiceTiming']>) {
  if (timing.status === 'PADDED') return `Nghỉ +${timing.paddingSeconds.toFixed(1)}s`
  if (timing.status === 'GAP_FITTED') return `Mượn ${timing.borrowedGapSeconds.toFixed(1)}s`
  if (timing.status === 'COMPRESSED') return `Tăng ${timing.appliedTempo?.toFixed(2)}x`
  if (timing.status === 'REVIEW_REQUIRED') return `Quá dài · ${timing.requiredTempo.toFixed(2)}x`
  if (timing.status === 'INVALID') return 'Sai thời lượng'
  return 'Tự nhiên 1.0x'
}
