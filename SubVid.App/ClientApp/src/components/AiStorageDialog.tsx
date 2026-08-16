import { useEffect, useRef } from 'react'
import { ArrowRight, Copy, Database, FolderInput, ShieldCheck, X } from 'lucide-react'

export type AiStorageSelection = {
  currentPath: string
  destinationPath: string
}

type AiStorageProgress = {
  percent: number
  message: string
}

type AiStorageDialogProps = {
  selection: AiStorageSelection | null
  busy: boolean
  progress: AiStorageProgress | null
  onClose: () => void
  onConfirm: (migrateExisting: boolean) => void
}

export function AiStorageDialog({
  selection,
  busy,
  progress,
  onClose,
  onConfirm,
}: AiStorageDialogProps) {
  const migrateButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!selection || busy) return
    const previousFocus = document.activeElement as HTMLElement | null
    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', keydown)
    window.setTimeout(() => migrateButtonRef.current?.focus(), 0)
    return () => {
      window.removeEventListener('keydown', keydown)
      previousFocus?.focus()
    }
  }, [busy, onClose, selection])

  if (!selection) return null

  const percent = Math.max(0, Math.min(100, progress?.percent ?? 0))

  return (
    <div
      className="import-dialog-backdrop ai-storage-dialog-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (!busy && event.target === event.currentTarget) onClose()
      }}
    >
      <div
        className="import-dialog ai-storage-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="ai-storage-dialog-title"
        aria-describedby="ai-storage-dialog-description"
      >
        <header>
          <div>
            <span><FolderInput size={15} /> Vị trí lưu AI local</span>
            <h2 id="ai-storage-dialog-title">Chuyển runtime và model sang thư mục mới?</h2>
            <p id="ai-storage-dialog-description">
              Chọn cách sử dụng thư mục vừa chọn. Dữ liệu tại vị trí cũ không bị xóa.
            </p>
          </div>
          <button type="button" disabled={busy} aria-label="Đóng" onClick={onClose}>
            <X size={18} />
          </button>
        </header>

        <div className="ai-storage-paths">
          <div>
            <small>Đang sử dụng</small>
            <code title={selection.currentPath}>{selection.currentPath}</code>
          </div>
          <ArrowRight size={18} aria-hidden="true" />
          <div>
            <small>Thư mục mới</small>
            <code title={selection.destinationPath}>{selection.destinationPath}</code>
          </div>
        </div>

        {busy ? (
          <div className="ai-storage-migration-progress" aria-live="polite">
            <div>
              <strong>Đang chuyển dữ liệu an toàn</strong>
              <span>{Math.round(percent)}%</span>
            </div>
            <div className="ai-storage-migration-progress__track">
              <span style={{ width: `${percent}%` }} />
            </div>
            <p>{progress?.message ?? 'Đang kiểm tra thư mục và dung lượng trống.'}</p>
          </div>
        ) : (
          <div className="ai-storage-options">
            <button
              ref={migrateButtonRef}
              type="button"
              onClick={() => onConfirm(true)}
            >
              <span className="ai-storage-option__icon"><Copy size={21} /></span>
              <strong>Sao chép dữ liệu hiện có <em>Khuyên dùng</em></strong>
              <small>
                Sao chép runtime và model, kiểm tra SHA-256 rồi mới đổi cấu hình. Có thể tiếp tục nếu bị gián đoạn.
              </small>
            </button>
            <button type="button" onClick={() => onConfirm(false)}>
              <span className="ai-storage-option__icon is-empty"><Database size={21} /></span>
              <strong>Dùng thư mục mới trống</strong>
              <small>
                Chuyển ngay sang vị trí mới. Runtime và model cần thiết sẽ được tải lại khi sử dụng.
              </small>
            </button>
          </div>
        )}

        <p className="ai-storage-dialog__safety">
          <ShieldCheck size={15} /> Đường dẫn chỉ được lưu sau khi toàn bộ bước kiểm tra hoàn tất.
        </p>
      </div>
    </div>
  )
}
