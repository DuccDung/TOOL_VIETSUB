import {
  Activity,
  BadgeCheck,
  CalendarDays,
  Check,
  Clock3,
  CreditCard,
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
  PlanCatalogItemInfo,
  UsageHistory,
} from '../types'

type AccountViewProps = {
  account: AccountInfo
  entitlements: EntitlementsInfo
  history: UsageHistory | null
  ffmpegStatus: FfmpegRuntimeStatus
  ffmpegProgress: FfmpegInstallProgress | null
  plans: PlanCatalogItemInfo[]
  plansLoading: boolean
  purchaseBusy: boolean
  onRefresh: () => void
  onLogout: () => void
  onManageFfmpeg: () => void
  onSelectFfmpegFolder: () => void
  onOpenFfmpegFolder: () => void
  onPurchasePlan: (plan: PlanCatalogItemInfo) => void
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

function formatPlanPrice(value: number, currency: string) {
  if (value <= 0) return 'Miễn phí'
  return new Intl.NumberFormat('vi-VN', {
    style: 'currency', currency, maximumFractionDigits: 0,
  }).format(value)
}

export function AccountView({
  account,
  entitlements,
  history,
  ffmpegStatus,
  ffmpegProgress,
  plans,
  plansLoading,
  purchaseBusy,
  onRefresh,
  onLogout,
  onManageFfmpeg,
  onSelectFfmpegFolder,
  onOpenFfmpegFolder,
  onPurchasePlan,
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

      <section className="account-plan-catalog">
        <header>
          <div><CreditCard size={18} /><span><strong>Gói SubVid</strong><small>Chọn quyền lợi phù hợp và thanh toán an toàn qua SePay</small></span></div>
          <span>{plansLoading ? 'Đang tải...' : `${plans.length} gói`}</span>
        </header>
        <div className="account-plan-grid">
          {plans.map((catalogPlan) => {
            const isCurrent = catalogPlan.code.toLocaleUpperCase() === plan.code.toLocaleUpperCase()
            const isFree = catalogPlan.priceAmount <= 0
            return (
              <article className={`account-plan-option ${isCurrent ? 'is-current' : ''}`} key={catalogPlan.code}>
                <div className="account-plan-option__heading">
                  <div><span>{catalogPlan.code}</span><h3>{catalogPlan.displayName}</h3></div>
                  {isCurrent ? <em>Đang sử dụng</em> : null}
                </div>
                <p>{catalogPlan.description}</p>
                <strong className="account-plan-option__price">
                  {formatPlanPrice(catalogPlan.priceAmount, catalogPlan.currencyCode)}
                  {!isFree ? <small> / {catalogPlan.billingPeriodDays} ngày</small> : null}
                </strong>
                <ul>
                  <li>{catalogPlan.monthlyQuotaMinutes?.toLocaleString('vi-VN') ?? 'Không giới hạn'} phút/tháng</li>
                  <li>Tối đa {catalogPlan.maxVideoMinutes?.toLocaleString('vi-VN') ?? 'không giới hạn'} phút/video</li>
                  <li>{catalogPlan.cloudOptions.length} cấu hình AI cloud</li>
                </ul>
                {!isFree ? (
                  <button
                    type="button"
                    disabled={isCurrent || purchaseBusy}
                    onClick={() => onPurchasePlan(catalogPlan)}
                  >
                    <CreditCard size={15} /> {isCurrent ? 'Gói hiện tại' : 'Mua hoặc nâng cấp'}
                  </button>
                ) : <span className="account-plan-option__free">Không cần thanh toán</span>}
              </article>
            )
          })}
          {!plansLoading && plans.length === 0 ? (
            <div className="account-empty-state"><CreditCard size={25} /><strong>Chưa tải được danh sách gói</strong><span>Hãy bấm Làm mới để thử lại.</span></div>
          ) : null}
        </div>
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
