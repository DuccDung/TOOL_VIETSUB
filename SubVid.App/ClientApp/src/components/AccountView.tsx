import {
  Activity,
  BadgeCheck,
  CalendarDays,
  Check,
  Clock3,
  Crown,
  History,
  Download,
  FolderOpen,
  HardDrive,
  LogOut,
  Mail,
  RefreshCw,
  ShieldCheck,
  Sparkles,
  UserRound,
} from 'lucide-react'
import type {
  AccountInfo,
  EntitlementsInfo,
  FfmpegInstallProgress,
  FfmpegRuntimeStatus,
  UsageHistory,
} from '../types'

type AccountViewProps = {
  account: AccountInfo
  entitlements: EntitlementsInfo
  history: UsageHistory | null
  ffmpegStatus: FfmpegRuntimeStatus
  ffmpegProgress: FfmpegInstallProgress | null
  onRefresh: () => void
  onLogout: () => void
  onManageFfmpeg: () => void
  onSelectFfmpegFolder: () => void
  onOpenFfmpegFolder: () => void
}

const featureLabels: Record<string, string> = {
  'subtitle.transcribe': 'Nhận dạng giọng nói',
  'subtitle.translate': 'Dịch phụ đề tiếng Việt',
  'voice.generate': 'Tạo giọng tiếng Việt',
  'ocr.detect': 'Nhận dạng phụ đề cứng',
  'video.export': 'Xuất video hoàn chỉnh',
  'batch.process': 'Xử lý hàng loạt',
}

const operationLabels: Record<string, string> = {
  STORAGE: 'Lưu trữ',
  TRANSCRIPTION: 'Nhận dạng',
  TRANSLATION: 'Dịch thuật',
  TTS: 'Tạo giọng',
  MEDIA_PROCESSING: 'Xử lý video',
  EGRESS: 'Xuất dữ liệu',
  OTHER: 'Tác vụ khác',
}

function formatDate(value: string | null) {
  if (!value) return 'Không giới hạn'
  return new Intl.DateTimeFormat('vi-VN', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}

function formatQuantity(value: number, unit: string) {
  const labels: Record<string, string> = {
    MINUTE: 'phút', SECOND: 'giây', CHARACTER: 'ký tự', TOKEN: 'token',
    BYTE: 'byte', REQUEST: 'lượt', FLAT: 'lượt',
  }
  return `${new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 2 }).format(value)} ${labels[unit] ?? unit}`
}

export function AccountView({
  account,
  entitlements,
  history,
  ffmpegStatus,
  ffmpegProgress,
  onRefresh,
  onLogout,
  onManageFfmpeg,
  onSelectFfmpegFolder,
  onOpenFfmpegFolder,
}: AccountViewProps) {
  const { plan, quota, features } = entitlements
  const quotaPercent = quota.monthlyMinutes && quota.monthlyMinutes > 0
    ? Math.min(100, ((quota.usedMinutes + quota.reservedMinutes) / quota.monthlyMinutes) * 100)
    : 0
  const initials = account.displayName
    .split(/\s+/)
    .slice(-2)
    .map((part) => part[0]?.toLocaleUpperCase('vi'))
    .join('')

  return (
    <main className="account-page" id="editor-workspace">
      <header className="account-heading">
        <div>
          <span className="account-eyebrow"><UserRound size={14} /> Tài khoản &amp; gói sử dụng</span>
          <h1>Không gian tài khoản</h1>
          <p>Theo dõi quyền, hạn mức và hoạt động đồng bộ với Server.</p>
        </div>
        <div className="account-heading__actions">
          <button type="button" className="account-secondary-button" onClick={onRefresh}>
            <RefreshCw size={15} /> Làm mới
          </button>
          <button type="button" className="account-logout-button" onClick={onLogout}>
            <LogOut size={15} /> Đăng xuất
          </button>
        </div>
      </header>

      <section className="account-summary-grid">
        <article className="account-card account-profile-card">
          <div className="account-avatar">{initials || 'TV'}</div>
          <div className="account-profile-copy">
            <span className="account-card-label">Hồ sơ</span>
            <h2>{account.displayName}</h2>
            <p><Mail size={13} /> {account.email}</p>
            <div className="account-badges">
              <span><ShieldCheck size={12} /> {account.role === 'ADMIN' ? 'Quản trị viên' : 'Người dùng'}</span>
              <span className="is-success"><BadgeCheck size={12} /> Đang hoạt động</span>
            </div>
          </div>
        </article>

        <article className="account-card plan-card">
          <div className="account-card-icon"><Crown size={20} /></div>
          <div>
            <span className="account-card-label">Gói hiện tại</span>
            <h2>{plan.displayName}</h2>
            <p>{plan.description}</p>
          </div>
          <span className="plan-code"><Sparkles size={11} /> {plan.code}</span>
        </article>

        <article className="account-card quota-card">
          <div className="quota-card__top">
            <div className="account-card-icon"><Activity size={20} /></div>
            <span>{quotaPercent.toFixed(0)}%</span>
          </div>
          <span className="account-card-label">Hạn mức tháng này</span>
          <h2>
            {quota.usedMinutes.toLocaleString('vi-VN')}
            <small> / {quota.monthlyMinutes?.toLocaleString('vi-VN') ?? '∞'} phút</small>
          </h2>
          <div className="quota-progress" aria-label={`Đã sử dụng ${quotaPercent.toFixed(0)}%`}>
            <span style={{ width: `${quotaPercent}%` }} />
          </div>
          <p>Còn lại {quota.remainingMinutes?.toLocaleString('vi-VN') ?? 'không giới hạn'} phút</p>
          {quota.reservedMinutes > 0 ? <p>Đang giữ {quota.reservedMinutes.toLocaleString('vi-VN')} phút cho công việc đang chạy</p> : null}
        </article>
      </section>

      <section className="account-content-grid">
        <article className="account-panel permissions-panel">
          <header>
            <div><ShieldCheck size={17} /><span><strong>Quyền sử dụng</strong><small>Tính năng được Server cấp cho tài khoản</small></span></div>
            <span>{features.length} quyền</span>
          </header>
          <div className="permission-list">
            {Object.entries(featureLabels).map(([code, label]) => {
              const enabled = features.includes(code)
              return (
                <div key={code} className={enabled ? 'is-enabled' : 'is-disabled'}>
                  <span className="permission-check">{enabled ? <Check size={13} /> : '—'}</span>
                  <span><strong>{label}</strong><small>{code}</small></span>
                  <em>{enabled ? 'Đã mở' : 'Chưa mở'}</em>
                </div>
              )
            })}
          </div>
        </article>

        <article className="account-panel account-details-panel">
          <header>
            <div><CalendarDays size={17} /><span><strong>Thông tin gói</strong><small>Chu kỳ và giới hạn xử lý</small></span></div>
          </header>
          <dl>
            <div><dt>Trạng thái</dt><dd><span className="status-dot" /> Đang hoạt động</dd></div>
            <div><dt>Bắt đầu</dt><dd>{formatDate(plan.startsAtUtc)}</dd></div>
            <div><dt>Hết hạn</dt><dd>{formatDate(plan.endsAtUtc)}</dd></div>
            <div><dt>Video tối đa</dt><dd>{quota.maxVideoMinutes ?? '∞'} phút/video</dd></div>
            <div><dt>Đồng bộ lúc</dt><dd>{formatDate(entitlements.evaluatedAtUtc)}</dd></div>
          </dl>
          <div className="security-callout">
            <ShieldCheck size={17} />
            <div><strong>Phiên này đang được bảo vệ</strong><span>Refresh token được mã hóa bằng Windows DPAPI.</span></div>
          </div>
        </article>

        <article className="account-panel video-tools-panel">
          <header>
            <div><HardDrive size={17} /><span><strong>Công cụ video</strong><small>FFmpeg và FFprobe chạy trực tiếp trên máy</small></span></div>
            <span className={ffmpegStatus.ready ? 'tool-status is-ready' : 'tool-status is-missing'}>
              {ffmpegProgress ? `${Math.round(ffmpegProgress.percent)}%` : ffmpegStatus.ready ? 'Sẵn sàng' : 'Chưa cài'}
            </span>
          </header>
          <div className="video-tools-body">
            <div className="video-tools-copy">
              <span className="video-tools-icon"><HardDrive size={21} /></span>
              <div>
                <strong>{ffmpegStatus.ready ? `FFmpeg ${ffmpegStatus.version ?? 'đã nhận diện'}` : `FFmpeg ${ffmpegStatus.targetVersion}`}</strong>
                <small>
                  {ffmpegProgress?.message
                    ?? (ffmpegStatus.ready
                      ? ffmpegStatus.source === 'MANAGED' ? 'Bản do SubVid quản lý và đã xác minh.' : 'Đang dùng bản FFmpeg bên ngoài.'
                      : 'Cần cài để nhập, xử lý và xuất video.')}
                </small>
              </div>
            </div>
            <div className="video-tools-actions">
              <button type="button" onClick={onManageFfmpeg} disabled={ffmpegProgress !== null}>
                <Download size={14} />
                {ffmpegStatus.ready
                  ? ffmpegStatus.version && ffmpegStatus.version !== ffmpegStatus.targetVersion ? 'Cập nhật' : 'Cài lại'
                  : 'Cài đặt'}
              </button>
              <button type="button" onClick={onSelectFfmpegFolder} disabled={ffmpegProgress !== null}>
                <FolderOpen size={14} /> Chọn thủ công
              </button>
              <button type="button" onClick={onOpenFfmpegFolder}>
                <FolderOpen size={14} /> Mở thư mục
              </button>
            </div>
          </div>
        </article>

        <article className="account-panel history-panel">
          <header>
            <div><History size={17} /><span><strong>Lịch sử sử dụng</strong><small>Các tác vụ đã đồng bộ gần đây</small></span></div>
            <span>{history?.totalCount ?? 0} sự kiện</span>
          </header>
          {history?.items.length ? (
            <div className="usage-list">
              {history.items.slice(0, 8).map((item) => (
                <div key={item.eventId}>
                  <span className="usage-icon"><Clock3 size={14} /></span>
                  <span><strong>{operationLabels[item.operationCode] ?? item.operationCode}</strong><small>{formatDate(item.occurredAtUtc)}</small></span>
                  <em>{formatQuantity(item.quantity, item.unitCode)}</em>
                </div>
              ))}
            </div>
          ) : (
            <div className="account-empty-state">
              <History size={25} />
              <strong>Chưa có lịch sử sử dụng</strong>
              <span>Các tác vụ video sẽ xuất hiện tại đây sau khi được đồng bộ.</span>
            </div>
          )}
        </article>
      </section>
    </main>
  )
}
