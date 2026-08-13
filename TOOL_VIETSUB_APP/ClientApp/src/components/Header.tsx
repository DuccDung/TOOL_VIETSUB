import {
  AudioLines,
  Captions,
  ChevronDown,
  Download,
  Gauge,
  FolderOpen,
  Library,
  Maximize2,
  Minimize2,
  Minus,
  Settings,
  SlidersHorizontal,
  Sparkles,
  Upload,
  UserRound,
  Video,
  X,
} from 'lucide-react'
import { postToHost } from '../lib/host'
import type { ProjectInfo, VideoInfo } from '../types'
import type { AccountInfo } from '../types'
import { IconButton } from './Ui'

type HeaderProps = {
  video: VideoInfo | null
  maximized: boolean
  activeNav: string
  account: AccountInfo
  currentProject: ProjectInfo | null
  onNavChange: (nav: string) => void
  onOpenProjects: () => void
  onOpenVideo: () => void
  onExportVideo: () => void
  onNotify: (title: string, description: string) => void
}

const navItems = [
  { id: 'subtitle', label: 'Phụ đề', icon: Captions },
  { id: 'voice', label: 'Giọng nói', icon: AudioLines },
  { id: 'library', label: 'Kho giọng', icon: Library },
  { id: 'downloads', label: 'Tải về', icon: Download },
  { id: 'account', label: 'Tài khoản', icon: UserRound },
]

export function Header({
  video,
  maximized,
  activeNav,
  account,
  currentProject,
  onNavChange,
  onOpenProjects,
  onOpenVideo,
  onExportVideo,
  onNotify,
}: HeaderProps) {
  return (
    <header className="app-header">
      <div className="title-bar">
        <div className="title-brand">
          <span className="app-mark"><Captions size={15} /></span>
          <strong>TOOL VIETSUB</strong>
          <span className="studio-label">STUDIO</span>
          <span className="version-label">V1.0</span>
          <span className="preview-badge"><Sparkles size={12} /> Bản thử nghiệm</span>
        </div>

        <div
          className="title-drag-zone"
          onPointerDown={() => postToHost('window:drag')}
          aria-hidden="true"
        />

        <div className="title-status">
          <span className="status-dot" />
          <span>{account.displayName}</span>
        </div>

        <div className="window-actions">
          <button
            type="button"
            aria-label="Thu nhỏ cửa sổ"
            title="Thu nhỏ"
            onClick={() => postToHost('window:minimize')}
          >
            <Minus size={15} />
          </button>
          <button
            type="button"
            aria-label={maximized ? 'Khôi phục cửa sổ' : 'Phóng to cửa sổ'}
            title={maximized ? 'Khôi phục' : 'Phóng to'}
            onClick={() => postToHost('window:maximize')}
          >
            {maximized ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
          </button>
          <button
            type="button"
            className="window-close"
            aria-label="Đóng ứng dụng"
            title="Đóng"
            onClick={() => postToHost('window:close')}
          >
            <X size={16} />
          </button>
        </div>
      </div>

      <div className="navigation-bar">
        <button
          type="button"
          className="nav-brand project-switcher"
          onClick={onOpenProjects}
          title={currentProject ? `Dự án: ${currentProject.name}` : 'Mở danh sách dự án'}
        >
          <span className="nav-brand__icon"><Video size={18} /></span>
          <span>Dự án</span>
          <FolderOpen size={13} />
        </button>

        <nav className="primary-nav" aria-label="Điều hướng chính">
          {navItems.map(({ id, label, icon: Icon }) => (
            <button
              key={id}
              type="button"
              className={activeNav === id ? 'is-active' : ''}
              aria-current={activeNav === id ? 'page' : undefined}
              onClick={() => {
                onNavChange(id)
                if (id !== 'subtitle' && id !== 'account') {
                  onNotify(label, 'Màn hình này sẽ được hoàn thiện ở bước UI tiếp theo.')
                }
              }}
            >
              <Icon size={19} strokeWidth={1.7} />
              <span>{label}</span>
            </button>
          ))}
        </nav>

        <button type="button" className="import-button" onClick={onOpenVideo}>
          <Video size={17} />
          <span>{video ? video.fileName : 'Nhập video'}</span>
          <small>{video ? 'Đã sẵn sàng' : '00:00'}</small>
          <ChevronDown size={14} />
        </button>

        <div className="navigation-actions">
          <IconButton
            label="Cài đặt"
            onClick={() => onNotify('Cài đặt', 'Bảng cài đặt đang ở chế độ xem trước UI.')}
          >
            <Settings size={18} />
          </IconButton>
          <IconButton
            label="Hiệu suất xử lý"
            onClick={() => onNotify('Hiệu suất', 'Chưa có tác vụ video đang chạy.')}
          >
            <Gauge size={18} />
          </IconButton>
          <IconButton
            label="Thiết lập nhanh"
            onClick={() => onNotify('Thiết lập nhanh', 'Tính năng sẽ được nối sau giai đoạn UI.')}
          >
            <SlidersHorizontal size={18} />
          </IconButton>
          <button
            type="button"
            className="export-button"
            disabled={!video}
            onClick={onExportVideo}
          >
            <Upload size={17} />
            <span>Xuất video</span>
          </button>
        </div>
      </div>
    </header>
  )
}
