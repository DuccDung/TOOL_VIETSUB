import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  FileVideo2,
  FolderOpen,
  LoaderCircle,
  PencilLine,
  Plus,
  X,
} from 'lucide-react'
import type { ProjectInfo, ProjectSummaryInfo } from '../types'

type ProjectDialogProps = {
  open: boolean
  projects: ProjectSummaryInfo[]
  currentProject: ProjectInfo | null
  busy: boolean
  error: string | null
  onClose: () => void
  onCreate: (name: string) => void
  onOpen: (projectId: string) => void
  onRename: (name: string) => void
}

const focusableSelector = [
  'button:not([disabled])',
  'input:not([disabled])',
  '[href]',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

export function ProjectDialog({
  open,
  projects,
  currentProject,
  busy,
  error,
  onClose,
  onCreate,
  onOpen,
  onRename,
}: ProjectDialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null)
  const createInputRef = useRef<HTMLInputElement>(null)
  const [newName, setNewName] = useState('')
  const [renameValue, setRenameValue] = useState(currentProject?.name ?? '')

  useEffect(() => {
    if (!open) return
    setRenameValue(currentProject?.name ?? '')
    const previousFocus = document.activeElement as HTMLElement | null
    window.setTimeout(() => createInputRef.current?.focus(), 0)
    return () => previousFocus?.focus()
  }, [open, currentProject?.name])

  if (!open) return null

  const handleDialogKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape' && !busy) {
      event.preventDefault()
      onClose()
      return
    }

    if (event.key !== 'Tab' || !dialogRef.current) return
    const focusable = Array.from(
      dialogRef.current.querySelectorAll<HTMLElement>(focusableSelector),
    )
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  const submitCreate = (event: FormEvent) => {
    event.preventDefault()
    const normalized = newName.trim()
    if (!normalized || busy) return
    onCreate(normalized)
  }

  return (
    <div className="workspace-dialog-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget && !busy) onClose()
    }}>
      <div
        ref={dialogRef}
        className="workspace-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="project-dialog-title"
        aria-describedby="project-dialog-description"
        onKeyDown={handleDialogKeyDown}
      >
        <header className="workspace-dialog__header">
          <div>
            <span className="workspace-dialog__eyebrow"><FolderOpen size={14} /> Không gian làm việc</span>
            <h2 id="project-dialog-title">Dự án video</h2>
            <p id="project-dialog-description">
              Mỗi dự án giữ video nguồn, phụ đề, audio và lịch sử xử lý riêng biệt.
            </p>
          </div>
          <button type="button" className="workspace-dialog__close" aria-label="Đóng" onClick={onClose} disabled={busy}>
            <X size={18} />
          </button>
        </header>

        {error ? <div className="workspace-dialog__error" role="alert"><AlertTriangle size={16} /> {error}</div> : null}

        <div className="workspace-dialog__body">
          <section className="project-create-card" aria-labelledby="create-project-title">
            <div className="project-section-title">
              <span><Plus size={15} /></span>
              <div>
                <h3 id="create-project-title">Tạo dự án mới</h3>
                <p>Dự án được đồng bộ với Server trước khi tạo workspace cục bộ.</p>
              </div>
            </div>
            <form onSubmit={submitCreate}>
              <label htmlFor="project-new-name">Tên dự án</label>
              <div className="project-create-row">
                <input
                  ref={createInputRef}
                  id="project-new-name"
                  value={newName}
                  maxLength={120}
                  placeholder="Ví dụ: Video giới thiệu sản phẩm"
                  onChange={(event) => setNewName(event.target.value)}
                  disabled={busy}
                  required
                />
                <button type="submit" disabled={busy || !newName.trim()}>
                  {busy ? <LoaderCircle className="spin" size={16} /> : <Plus size={16} />}
                  Tạo dự án
                </button>
              </div>
            </form>
          </section>

          <section className="project-list-section" aria-labelledby="recent-projects-title">
            <div className="project-list-heading">
              <div>
                <h3 id="recent-projects-title">Dự án gần đây</h3>
                <p>{projects.length} dự án trên máy của tài khoản hiện tại</p>
              </div>
              {currentProject ? <span className="project-current-badge"><CheckCircle2 size={13} /> Đang mở</span> : null}
            </div>

            <div className="project-list" role="list">
              {projects.length === 0 ? (
                <div className="project-list-empty">
                  <FolderOpen size={26} />
                  <strong>Chưa có dự án cục bộ</strong>
                  <span>Tạo dự án đầu tiên để bắt đầu nhập video.</span>
                </div>
              ) : projects.map((project) => {
                const active = currentProject?.projectId === project.projectId
                return (
                  <button
                    key={project.projectId}
                    type="button"
                    role="listitem"
                    className={`project-list-item ${active ? 'is-active' : ''}`}
                    onClick={() => onOpen(project.projectId)}
                    disabled={busy || active}
                  >
                    <span className="project-list-item__icon"><FileVideo2 size={19} /></span>
                    <span className="project-list-item__content">
                      <strong>{project.name}</strong>
                      <small>
                        {project.sourceFileName ?? 'Chưa nhập video'}
                        {project.durationSeconds ? ` · ${(project.durationSeconds / 60).toFixed(1)} phút` : ''}
                      </small>
                    </span>
                    <span className="project-list-item__meta">
                      {project.needsRecovery ? <em><AlertTriangle size={12} /> Cần phục hồi</em> : null}
                      <small><Clock3 size={12} /> {new Date(project.updatedAtUtc).toLocaleDateString('vi-VN')}</small>
                    </span>
                  </button>
                )
              })}
            </div>
          </section>

          {currentProject ? (
            <section className="project-rename-card" aria-labelledby="rename-project-title">
              <div>
                <h3 id="rename-project-title"><PencilLine size={14} /> Đổi tên dự án đang mở</h3>
                <p>Video và dữ liệu trong workspace không bị thay đổi.</p>
              </div>
              <div className="project-rename-row">
                <label className="sr-only" htmlFor="project-rename">Tên mới</label>
                <input
                  id="project-rename"
                  value={renameValue}
                  maxLength={120}
                  onChange={(event) => setRenameValue(event.target.value)}
                  disabled={busy}
                />
                <button
                  type="button"
                  disabled={busy || !renameValue.trim() || renameValue.trim() === currentProject.name}
                  onClick={() => onRename(renameValue.trim())}
                >
                  Lưu tên
                </button>
              </div>
            </section>
          ) : null}
        </div>
      </div>
    </div>
  )
}
