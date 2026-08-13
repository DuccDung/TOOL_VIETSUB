import {
  ClipboardEvent,
  FormEvent,
  KeyboardEvent,
  useEffect,
  useRef,
  useState,
} from 'react'
import {
  ArrowLeft,
  Captions,
  Check,
  Clock3,
  Eye,
  EyeOff,
  KeyRound,
  LoaderCircle,
  LockKeyhole,
  MailCheck,
  Maximize2,
  Minimize2,
  Minus,
  RefreshCw,
  ShieldCheck,
  Sparkles,
  UserRound,
  WandSparkles,
  X,
} from 'lucide-react'
import { postToHost } from '../lib/host'
import type { AuthState, RegistrationState } from '../types'

type AuthMode = 'login' | 'register'

type AuthScreenProps = {
  authState: AuthState
  registrationState: RegistrationState
  initialMode: AuthMode
  maximized: boolean
  onLogin: (email: string, password: string) => void
  onRegister: (displayName: string, email: string, password: string) => void
  onVerifyOtp: (challengeId: string, otp: string) => void
  onResendOtp: (challengeId: string) => void
  onResetRegistration: () => void
  onRetry: () => void
}

const emptyOtp = ['', '', '', '', '', '']

function formatCountdown(totalSeconds: number) {
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

export function AuthScreen({
  authState,
  registrationState,
  initialMode,
  maximized,
  onLogin,
  onRegister,
  onVerifyOtp,
  onResendOtp,
  onResetRegistration,
  onRetry,
}: AuthScreenProps) {
  const [mode, setMode] = useState<AuthMode>(initialMode)
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [otpDigits, setOtpDigits] = useState<string[]>(emptyOtp)
  const [localError, setLocalError] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())
  const otpRefs = useRef<Array<HTMLInputElement | null>>([])
  const challenge = registrationState.challenge
  const isLoginLoading = mode === 'login' && authState.status === 'loading'

  useEffect(() => {
    if (!challenge) return
    setOtpDigits(emptyOtp)
    setLocalError(null)
    setNow(Date.now())
    window.setTimeout(() => otpRefs.current[0]?.focus(), 80)
  }, [challenge?.challengeId])

  useEffect(() => {
    if (!challenge) return
    const timer = window.setInterval(() => setNow(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [challenge])

  const expiresIn = challenge
    ? Math.max(0, Math.ceil((new Date(challenge.expiresAtUtc).getTime() - now) / 1_000))
    : 0
  const resendIn = challenge
    ? Math.max(0, Math.ceil((new Date(challenge.resendAtUtc).getTime() - now) / 1_000))
    : 0

  const switchMode = (nextMode: AuthMode) => {
    setMode(nextMode)
    setLocalError(null)
    if (nextMode === 'login') onResetRegistration()
  }

  const submitLogin = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const normalizedEmail = email.trim()
    if (!normalizedEmail || !normalizedEmail.includes('@')) {
      setLocalError('Hãy nhập địa chỉ email hợp lệ.')
      return
    }
    if (password.length < 8) {
      setLocalError('Mật khẩu phải có ít nhất 8 ký tự.')
      return
    }

    setLocalError(null)
    onLogin(normalizedEmail, password)
  }

  const submitRegistration = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const normalizedName = displayName.trim()
    const normalizedEmail = email.trim()
    if (normalizedName.length < 2) {
      setLocalError('Họ tên phải có ít nhất 2 ký tự.')
      return
    }
    if (!normalizedEmail || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalizedEmail)) {
      setLocalError('Hãy nhập địa chỉ email hợp lệ.')
      return
    }
    if (password.length < 8) {
      setLocalError('Mật khẩu phải có ít nhất 8 ký tự.')
      return
    }
    if (password !== confirmPassword) {
      setLocalError('Mật khẩu xác nhận chưa trùng khớp.')
      return
    }

    setLocalError(null)
    onRegister(normalizedName, normalizedEmail, password)
  }

  const setOtpFrom = (startIndex: number, value: string) => {
    const numbers = value.replace(/\D/g, '').slice(0, 6 - startIndex).split('')
    if (numbers.length === 0) {
      setOtpDigits((current) => current.map((digit, index) => index === startIndex ? '' : digit))
      return
    }

    setOtpDigits((current) => {
      const next = [...current]
      numbers.forEach((number, offset) => { next[startIndex + offset] = number })
      return next
    })
    otpRefs.current[Math.min(startIndex + numbers.length, 5)]?.focus()
  }

  const handleOtpKeyDown = (index: number, event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Backspace' && !otpDigits[index] && index > 0) {
      otpRefs.current[index - 1]?.focus()
    }
    if (event.key === 'ArrowLeft' && index > 0) otpRefs.current[index - 1]?.focus()
    if (event.key === 'ArrowRight' && index < 5) otpRefs.current[index + 1]?.focus()
  }

  const handleOtpPaste = (event: ClipboardEvent<HTMLInputElement>) => {
    event.preventDefault()
    setOtpFrom(0, event.clipboardData.getData('text'))
  }

  const submitOtp = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!challenge) return
    const otp = otpDigits.join('')
    if (expiresIn === 0) {
      setLocalError('Mã OTP đã hết hạn. Hãy bắt đầu đăng ký lại.')
      return
    }
    if (otp.length !== 6) {
      setLocalError('Hãy nhập đủ 6 chữ số trong email.')
      return
    }

    setLocalError(null)
    onVerifyOtp(challenge.challengeId, otp)
  }

  const visibleError = localError
    ?? registrationState.errorMessage
    ?? (mode === 'login' ? authState.errorMessage : null)
  const busyLabel = registrationState.operation === 'verify'
    ? 'Đang xác minh OTP...'
    : registrationState.operation === 'resend'
      ? 'Đang gửi lại email...'
      : 'Đang gửi mã xác nhận...'

  return (
    <div className="auth-shell">
      <header className="auth-title-bar">
        <div className="title-brand">
          <span className="app-mark"><Captions size={15} /></span>
          <strong>TOOL VIETSUB</strong>
          <span className="studio-label">STUDIO</span>
          <span className="version-label">V1.0</span>
          <span className="preview-badge"><Sparkles size={12} /> Bản thử nghiệm</span>
        </div>
        <div className="title-drag-zone" onPointerDown={() => postToHost('window:drag')} aria-hidden="true" />
        <div className="auth-secure-label"><ShieldCheck size={13} /> Kết nối bảo mật</div>
        <div className="window-actions">
          <button type="button" aria-label="Thu nhỏ cửa sổ" onClick={() => postToHost('window:minimize')}><Minus size={15} /></button>
          <button type="button" aria-label={maximized ? 'Khôi phục cửa sổ' : 'Phóng to cửa sổ'} onClick={() => postToHost('window:maximize')}>
            {maximized ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
          </button>
          <button type="button" className="window-close" aria-label="Đóng ứng dụng" onClick={() => postToHost('window:close')}><X size={16} /></button>
        </div>
      </header>

      <main className="auth-stage">
        <div className="auth-glow auth-glow--one" />
        <div className="auth-glow auth-glow--two" />

        <section className="auth-intro" aria-label="Giới thiệu TOOL VIETSUB">
          <span className="auth-eyebrow"><WandSparkles size={14} /> Vietnamese AI Studio</span>
          <h1>Biến video thành nội dung tiếng Việt trong một quy trình.</h1>
          <p>Nhận dạng lời nói, biên tập phụ đề, tạo giọng Việt và kiểm soát toàn bộ tiến trình ngay trên máy của bạn.</p>

          <div className="auth-feature-list">
            <div><span><Check size={14} /></span><div><strong>Xử lý cục bộ</strong><small>Video gốc không phải tải lên Server.</small></div></div>
            <div><span><Check size={14} /></span><div><strong>Phiên đăng nhập an toàn</strong><small>Token được bảo vệ bởi Windows.</small></div></div>
            <div><span><Check size={14} /></span><div><strong>Quyền sử dụng rõ ràng</strong><small>Gói và hạn mức được đồng bộ tức thời.</small></div></div>
          </div>

          <div className="auth-pipeline" aria-label="Quy trình xử lý">
            <span>Video</span><i /><span>Phụ đề</span><i /><span>Giọng Việt</span><i /><span>Xuất bản</span>
          </div>
        </section>

        <section className={`auth-card ${challenge ? 'auth-card--otp' : ''}`} aria-labelledby="auth-title">
          {!challenge ? (
            <div className="auth-tabs" role="tablist" aria-label="Tài khoản">
              <button type="button" role="tab" aria-selected={mode === 'login'} className={mode === 'login' ? 'is-active' : ''} onClick={() => switchMode('login')}>Đăng nhập</button>
              <button type="button" role="tab" aria-selected={mode === 'register'} className={mode === 'register' ? 'is-active' : ''} onClick={() => switchMode('register')}>Đăng ký</button>
            </div>
          ) : null}

          <div className="auth-card__top">
            <div className="auth-card__mark">{challenge ? <MailCheck size={23} /> : <KeyRound size={23} />}</div>
            <div className="auth-card__heading">
              <span>{challenge ? 'Xác minh email' : 'Không gian làm việc'}</span>
              <h2 id="auth-title">{challenge ? 'Nhập mã OTP' : mode === 'login' ? 'Đăng nhập TOOL VIETSUB' : 'Tạo tài khoản mới'}</h2>
              <p>{challenge ? <>Mã 6 số đã được gửi tới <strong>{challenge.maskedEmail}</strong>.</> : mode === 'login' ? 'Đăng nhập để tiếp tục công việc của bạn.' : 'Tài khoản mới bắt đầu với gói FREE.'}</p>
            </div>
          </div>

          {isLoginLoading ? (
            <div className="auth-loading" role="status">
              <LoaderCircle size={28} className="spin" />
              <strong>Đang kiểm tra phiên đăng nhập</strong>
              <span>Ứng dụng đang kết nối an toàn tới Server...</span>
            </div>
          ) : challenge ? (
            <form className="auth-form auth-form--otp" onSubmit={submitOtp} noValidate>
              {visibleError ? <AuthAlert title="Chưa thể xác minh" message={visibleError} /> : null}
              <div className="otp-inputs" role="group" aria-label="Mã OTP gồm 6 chữ số">
                {otpDigits.map((digit, index) => (
                  <input
                    key={index}
                    ref={(element) => { otpRefs.current[index] = element }}
                    type="text"
                    inputMode="numeric"
                    autoComplete={index === 0 ? 'one-time-code' : 'off'}
                    maxLength={1}
                    value={digit}
                    aria-label={`Chữ số OTP ${index + 1}`}
                    disabled={registrationState.busy}
                    onFocus={(event) => event.currentTarget.select()}
                    onChange={(event) => setOtpFrom(index, event.target.value)}
                    onKeyDown={(event) => handleOtpKeyDown(index, event)}
                    onPaste={handleOtpPaste}
                  />
                ))}
              </div>

              <div className={`otp-expiry ${expiresIn === 0 ? 'is-expired' : ''}`}>
                <Clock3 size={14} />
                {expiresIn > 0 ? <>Mã còn hiệu lực <strong>{formatCountdown(expiresIn)}</strong></> : <strong>Mã OTP đã hết hạn</strong>}
              </div>

              <button className="auth-submit" type="submit" disabled={registrationState.busy || expiresIn === 0}>
                <span>{registrationState.busy ? busyLabel : 'Xác nhận và vào Studio'}</span>
                {registrationState.busy ? <LoaderCircle size={16} className="spin" /> : <span>→</span>}
              </button>

              <div className="otp-actions">
                <button
                  type="button"
                  disabled={registrationState.busy || resendIn > 0 || challenge.resendsRemaining <= 0}
                  onClick={() => onResendOtp(challenge.challengeId)}
                >
                  <RefreshCw size={13} />
                  {resendIn > 0 ? `Gửi lại sau ${resendIn}s` : challenge.resendsRemaining > 0 ? `Gửi lại OTP (${challenge.resendsRemaining})` : 'Đã hết lượt gửi lại'}
                </button>
                <button type="button" disabled={registrationState.busy} onClick={onResetRegistration}><ArrowLeft size={13} /> Đổi email</button>
              </div>
            </form>
          ) : mode === 'login' ? (
            <form className="auth-form" onSubmit={submitLogin} noValidate>
              {visibleError ? <AuthAlert title="Chưa thể đăng nhập" message={visibleError} /> : null}
              <AuthEmailField email={email} onChange={setEmail} />
              <AuthPasswordField password={password} showPassword={showPassword} autoComplete="current-password" onChange={setPassword} onToggle={() => setShowPassword((value) => !value)} />
              <div className="auth-device-note"><ShieldCheck size={15} /> Phiên đăng nhập được mã hóa và chỉ dùng được trên thiết bị này.</div>
              <button className="auth-submit" type="submit"><span>Đăng nhập vào Studio</span><span>→</span></button>
              {authState.status === 'error' ? <button className="auth-retry" type="button" onClick={onRetry}><RefreshCw size={14} /> Thử kết nối lại</button> : null}
            </form>
          ) : (
            <form className="auth-form auth-form--register" onSubmit={submitRegistration} noValidate>
              {visibleError ? <AuthAlert title="Chưa thể đăng ký" message={visibleError} /> : null}
              <label className="auth-field"><span>Họ và tên</span><div><UserRound size={17} /><input type="text" autoComplete="name" value={displayName} placeholder="Nguyễn Văn A" disabled={registrationState.busy} onChange={(event) => setDisplayName(event.target.value)} /></div></label>
              <AuthEmailField email={email} disabled={registrationState.busy} onChange={setEmail} />
              <div className="auth-field-row">
                <AuthPasswordField label="Mật khẩu" password={password} showPassword={showPassword} autoComplete="new-password" disabled={registrationState.busy} onChange={setPassword} onToggle={() => setShowPassword((value) => !value)} />
                <AuthPasswordField label="Xác nhận" password={confirmPassword} showPassword={showPassword} autoComplete="new-password" disabled={registrationState.busy} onChange={setConfirmPassword} onToggle={() => setShowPassword((value) => !value)} />
              </div>
              <div className="auth-device-note"><ShieldCheck size={15} /> OTP hết hạn sau 5 phút. Mật khẩu không được gửi qua email.</div>
              <button className="auth-submit" type="submit" disabled={registrationState.busy}>
                <span>{registrationState.busy ? busyLabel : 'Gửi mã xác nhận'}</span>
                {registrationState.busy ? <LoaderCircle size={16} className="spin" /> : <span>→</span>}
              </button>
            </form>
          )}

          <footer className="auth-card__footer"><span><span className="status-dot" /> Server bảo mật qua HTTPS</span><small>V1.0.0</small></footer>
        </section>
      </main>
    </div>
  )
}

function AuthAlert({ title, message }: { title: string, message: string }) {
  return <div className="auth-alert" role="alert"><span>!</span><div><strong>{title}</strong><p>{message}</p></div></div>
}

function AuthEmailField({ email, disabled = false, onChange }: { email: string, disabled?: boolean, onChange: (value: string) => void }) {
  return <label className="auth-field"><span>Email</span><div><UserRound size={17} /><input type="email" inputMode="email" autoComplete="username" value={email} placeholder="tenban@example.com" disabled={disabled} onChange={(event) => onChange(event.target.value)} /></div></label>
}

function AuthPasswordField({ label = 'Mật khẩu', password, showPassword, autoComplete, disabled = false, onChange, onToggle }: { label?: string, password: string, showPassword: boolean, autoComplete: string, disabled?: boolean, onChange: (value: string) => void, onToggle: () => void }) {
  return <label className="auth-field"><span>{label}</span><div><LockKeyhole size={17} /><input type={showPassword ? 'text' : 'password'} autoComplete={autoComplete} value={password} placeholder="Ít nhất 8 ký tự" disabled={disabled} onChange={(event) => onChange(event.target.value)} /><button type="button" aria-label={showPassword ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'} onClick={onToggle}>{showPassword ? <EyeOff size={17} /> : <Eye size={17} />}</button></div></label>
}
