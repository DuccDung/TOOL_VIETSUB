import { useEffect, useRef } from 'react'
import { Download, FileCheck2, FolderOpen, HardDrive, ShieldCheck, TriangleAlert, X } from 'lucide-react'
import { formatBytes } from '../lib/format'
import type { FfmpegInstallProgress, FfmpegRuntimeStatus } from '../types'

type FfmpegSetupDialogProps = {
  open: boolean
  status: FfmpegRuntimeStatus
  progress: FfmpegInstallProgress | null
  error: string | null
  pendingFileName: string | null
  force: boolean
  onInstall: () => void
  onSelectFolder: () => void
  onCancel: () => void
}

export function FfmpegSetupDialog({
  open,
  status,
  progress,
  error,
  pendingFileName,
  force,
  onInstall,
  onSelectFolder,
  onCancel,
}: FfmpegSetupDialogProps) {
  const installRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!open || progress) return
    window.setTimeout(() => installRef.current?.focus(), 0)
  }, [open, progress])

  if (!open) return null

  const busy = progress !== null && progress.phase !== 'READY'
  const title = force
    ? status.version && status.version !== status.targetVersion ? 'Cập nhật FFmpeg' : 'Cài lại FFmpeg'
    : 'Cài bộ công cụ video'

  return (
    <div className="ffmpeg-dialog-backdrop" role="presentation">
      <section className="ffmpeg-dialog" role="dialog" aria-modal="true" aria-labelledby="ffmpeg-dialog-title">
        <header>
          <div className="ffmpeg-dialog__mark"><Download size={22} /></div>
          <div>
            <span><ShieldCheck size={13} /> Tải trực tiếp · kiểm tra SHA-256</span>
            <h2 id="ffmpeg-dialog-title">{title}</h2>
          </div>
          <button type="button" aria-label="Đóng" onClick={onCancel}><X size={18} /></button>
        </header>

        <div className="ffmpeg-dialog__body">
          {pendingFileName ? (
            <p className="ffmpeg-dialog__pending">
              App cần FFmpeg để đọc <strong>{pendingFileName}</strong>. Sau khi cài xong, thao tác nhập video sẽ tự tiếp tục.
            </p>
          ) : (
            <p className="ffmpeg-dialog__pending">FFmpeg và FFprobe cung cấp khả năng đọc, xử lý và xuất video trên máy.</p>
          )}

          <div className="ffmpeg-package-card">
            <span><HardDrive size={18} /></span>
            <div>
              <strong>FFmpeg Essentials {status.targetVersion}</strong>
              <small>{formatBytes(status.downloadBytes)} · Windows x64 · {status.license}</small>
            </div>
            <FileCheck2 size={18} />
          </div>

          {progress ? (
            <div className="ffmpeg-progress" aria-live="polite">
              <div><strong>{progress.message}</strong><span>{Math.round(progress.percent)}%</span></div>
              <div className="ffmpeg-progress__track" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress.percent)}>
                <span style={{ width: `${progress.percent}%` }} />
              </div>
              <small>
                {progress.phase === 'DOWNLOAD'
                  ? `${formatBytes(progress.bytesProcessed)} / ${formatBytes(progress.totalBytes)}`
                  : 'Đang hoàn tất hoàn toàn trên máy của bạn'}
              </small>
            </div>
          ) : null}

          {error ? <div className="ffmpeg-dialog__error"><TriangleAlert size={16} /><span>{error}</span></div> : null}

          <div className="ffmpeg-security-note">
            <ShieldCheck size={16} />
            <span>Server SubVid không truyền file này. Gói tải về phải khớp checksum đã ghim mới được chạy.</span>
          </div>
        </div>

        <footer>
          <button type="button" className="secondary" onClick={onSelectFolder} disabled={busy}>
            <FolderOpen size={15} /> Chọn bản đã có
          </button>
          <span />
          <button type="button" className="secondary" onClick={onCancel}>{busy ? 'Hủy tải' : 'Để sau'}</button>
          {!busy ? (
            <button ref={installRef} type="button" className="primary" onClick={onInstall}>
              <Download size={15} /> {error ? 'Thử tải lại' : force ? 'Tiếp tục' : 'Tải và cài đặt'}
            </button>
          ) : null}
        </footer>
      </section>
    </div>
  )
}
