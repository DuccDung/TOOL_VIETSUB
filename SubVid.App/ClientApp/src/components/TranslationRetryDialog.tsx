import { useEffect, useRef } from 'react'
import { ArrowRight, RefreshCw, ShieldCheck, Sparkles, X } from 'lucide-react'

type TranslationRetryMode = 'continue' | 'restart'

type TranslationRetryDialogProps = {
  open: boolean
  errorMessage?: string | null
  completedCues?: number
  totalPendingCues?: number
  onClose: () => void
  onSelect: (mode: TranslationRetryMode) => void
}

export function TranslationRetryDialog({
  open,
  errorMessage,
  completedCues = 0,
  totalPendingCues = 0,
  onClose,
  onSelect,
}: TranslationRetryDialogProps) {
  const continueRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!open) return
    const previousFocus = document.activeElement as HTMLElement | null
    const keydown = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', keydown)
    window.setTimeout(() => continueRef.current?.focus(), 0)
    return () => {
      window.removeEventListener('keydown', keydown)
      previousFocus?.focus()
    }
  }, [open, onClose])

  if (!open) return null

  const remainingCues = Math.max(0, totalPendingCues - completedCues)

  return (
    <div
      className="import-dialog-backdrop translation-retry-dialog-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div
        className="import-dialog translation-retry-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="translation-retry-dialog-title"
        aria-describedby="translation-retry-dialog-description"
      >
        <header>
          <div>
            <span><ShieldCheck size={15} /> Khôi phục bản dịch an toàn</span>
            <h2 id="translation-retry-dialog-title">Bạn muốn xử lý phần dịch lỗi thế nào?</h2>
            <p id="translation-retry-dialog-description">
              Bản dịch đã lưu vẫn được giữ nguyên cho đến khi kết quả mới hợp lệ.
            </p>
          </div>
          <button type="button" aria-label="Đóng" onClick={onClose}><X size={18} /></button>
        </header>

        {errorMessage ? <p className="translation-retry-dialog__error">{errorMessage}</p> : null}

        <div className="translation-retry-options">
          <button ref={continueRef} type="button" onClick={() => onSelect('continue')}>
            <span className="translation-retry-option__icon"><ArrowRight size={21} /></span>
            <strong>Dịch tiếp <em>Khuyên dùng</em></strong>
            <small>
              Chỉ dịch cue còn thiếu hoặc bị lỗi. Giữ checkpoint hiện có và tiết kiệm token.
              {remainingCues > 0 ? ` Còn khoảng ${remainingCues} cue cần xử lý.` : ''}
            </small>
          </button>
          <button type="button" onClick={() => onSelect('restart')}>
            <span className="translation-retry-option__icon is-restart"><RefreshCw size={20} /></span>
            <strong>Dịch lại toàn bộ</strong>
            <small>
              Dịch lại tất cả cue do AI tạo và bỏ qua cache cũ. Cue chỉnh sửa thủ công vẫn được giữ nguyên.
            </small>
          </button>
        </div>

        <p className="translation-retry-dialog__note">
          <Sparkles size={14} /> Dịch lại toàn bộ sẽ sử dụng nhiều token hơn.
        </p>
      </div>
    </div>
  )
}
