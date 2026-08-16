import { useEffect, useRef, useState } from 'react'
import { Check, Copy, Link2, ShieldCheck, X } from 'lucide-react'

type ImportVideoDialogProps = {
  open: boolean
  onClose: () => void
  onContinue: (mode: 'copy' | 'link') => void
}

export function ImportVideoDialog({ open, onClose, onContinue }: ImportVideoDialogProps) {
  const [mode, setMode] = useState<'copy' | 'link'>('copy')
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

  return (
    <div className="import-dialog-backdrop" role="presentation">
      <div className="import-dialog" role="dialog" aria-modal="true" aria-labelledby="import-dialog-title">
        <header>
          <div>
            <span><ShieldCheck size={15} /> Bảo vệ video nguồn</span>
            <h2 id="import-dialog-title">Chọn cách nhập video</h2>
          </div>
          <button type="button" aria-label="Đóng" onClick={onClose}><X size={18} /></button>
        </header>

        <div className="import-mode-grid" role="radiogroup" aria-label="Chế độ nhập video">
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'copy'}
            className={mode === 'copy' ? 'is-selected' : ''}
            onClick={() => setMode('copy')}
          >
            <span className="import-mode-icon"><Copy size={20} /></span>
            <strong>Sao chép vào dự án <em>Khuyên dùng</em></strong>
            <small>An toàn khi file gốc bị di chuyển. Cần thêm dung lượng ổ đĩa.</small>
            {mode === 'copy' ? <Check className="import-mode-check" size={15} /> : null}
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'link'}
            className={mode === 'link' ? 'is-selected' : ''}
            onClick={() => setMode('link')}
          >
            <span className="import-mode-icon"><Link2 size={20} /></span>
            <strong>Liên kết file gốc</strong>
            <small>Không tốn thêm dung lượng nhưng dự án cần file luôn ở đúng vị trí.</small>
            {mode === 'link' ? <Check className="import-mode-check" size={15} /> : null}
          </button>
        </div>

        <p className="import-dialog-note">
          App chỉ đọc video gốc. Mọi audio, phụ đề và video xuất đều nằm trong workspace riêng.
        </p>
        <footer>
          <button type="button" className="secondary" onClick={onClose}>Hủy</button>
          <button ref={continueRef} type="button" className="primary" onClick={() => onContinue(mode)}>
            Chọn video
          </button>
        </footer>
      </div>
    </div>
  )
}
