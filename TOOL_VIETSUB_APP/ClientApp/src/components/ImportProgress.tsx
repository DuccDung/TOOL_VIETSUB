import { FileVideo2, LoaderCircle, X } from 'lucide-react'
import { formatBytes } from '../lib/format'
import type { MediaImportState } from '../types'

type ImportProgressProps = {
  state: MediaImportState
  onCancel: () => void
}

export function ImportProgress({ state, onCancel }: ImportProgressProps) {
  if (!state.active) return null

  return (
    <aside className="import-progress-card" aria-live="polite" aria-atomic="true">
      <span className="import-progress-card__icon"><LoaderCircle className="spin" size={19} /></span>
      <div className="import-progress-card__body">
        <div>
          <strong><FileVideo2 size={14} /> Đang kiểm tra và nhập video</strong>
          <span>{state.fileName} · {state.mode === 'COPY' ? 'Sao chép an toàn' : 'Liên kết nguồn'}</span>
        </div>
        <div className="import-progress-track" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(state.percent)}>
          <span style={{ width: `${state.percent}%` }} />
        </div>
        <small>
          {state.totalBytes > 0 ? `${formatBytes(state.bytesProcessed)} / ${formatBytes(state.totalBytes)}` : 'Đang đọc metadata'}
          {state.megabytesPerSecond > 0 ? ` · ${state.megabytesPerSecond.toFixed(1)} MB/s` : ''}
        </small>
      </div>
      <button type="button" aria-label="Hủy nhập video" title="Hủy nhập video" onClick={onCancel}><X size={17} /></button>
    </aside>
  )
}
