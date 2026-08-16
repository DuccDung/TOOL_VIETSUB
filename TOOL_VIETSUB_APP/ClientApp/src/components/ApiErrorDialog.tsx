import { useEffect, useRef, type KeyboardEvent } from 'react'
import { Cloud, KeyRound, RotateCcw, TriangleAlert, WifiOff, X } from 'lucide-react'

type ApiErrorDialogProps = {
  open: boolean
  provider: string
  code: string
  message: string
  onClose: () => void
}

const focusableSelector = [
  'button:not([disabled])',
  'input:not([disabled])',
  '[href]',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

function getGuidance(code: string) {
  if (code === 'TRANSLATION_API_KEY_REQUIRED' || code === 'TRANSLATION_API_KEY_INVALID'
    || code === 'FPT_API_KEY_REQUIRED' || code === 'FPT_API_KEY_INVALID') {
    return {
      icon: KeyRound,
      title: 'Kiểm tra API key',
      description: code.startsWith('FPT_')
        ? 'Hãy mở Kho giọng, nhập đúng API key của project đã bật Text to Speech trên FPT.AI rồi nghe thử lại.'
        : 'Hãy mở phần Cài đặt dịch, kiểm tra API key và lưu lại trước khi thử lại.',
    }
  }

  if (code === 'TRANSLATION_RATE_LIMITED' || code === 'FPT_RATE_LIMITED') {
    return {
      icon: RotateCcw,
      title: 'Kiểm tra quota và giới hạn gọi',
      description: 'Tài khoản có thể đã hết quota, chạm giới hạn tốc độ hoặc cần kiểm tra billing của nhà cung cấp.',
    }
  }

  if (code === 'TRANSLATION_BALANCE_EXHAUSTED') {
    return {
      icon: KeyRound,
      title: 'Kiểm tra số dư API',
      description: 'Tài khoản nhà cung cấp không còn số dư. Hãy kiểm tra billing hoặc nạp thêm trước khi thử lại.',
    }
  }

  if (code === 'FPT_QUOTA_EXCEEDED') {
    return {
      icon: KeyRound,
      title: 'Kiểm tra quota FPT.AI',
      description: 'Gói Text to Speech có thể đã hết ký tự miễn phí hoặc số dư. Hãy kiểm tra project và quota trên console.fpt.ai.',
    }
  }

  if (code === 'TRANSLATION_MODEL_UNAVAILABLE' || code === 'TRANSLATION_API_ACCESS_DENIED') {
    return {
      icon: Cloud,
      title: 'Kiểm tra model và quyền truy cập',
      description: 'Hãy chọn model đang hoạt động và kiểm tra model đã được bật cho project của nhà cung cấp.',
    }
  }

  if (code === 'TRANSLATION_REQUEST_TOO_LARGE') {
    return {
      icon: RotateCcw,
      title: 'Giảm kích thước cảnh dịch',
      description: 'Cảnh hoặc phần ngữ cảnh quá lớn. Hãy giảm glossary, bối cảnh hoặc số cue rồi thử lại.',
    }
  }

  if (code === 'TRANSLATION_NETWORK_ERROR' || code === 'TRANSLATION_PROVIDER_TIMEOUT'
    || code === 'FPT_NETWORK_ERROR' || code === 'FPT_NETWORK_TIMEOUT'
    || code === 'FPT_RESULT_TIMEOUT' || code === 'FPT_SERVICE_UNAVAILABLE') {
    return {
      icon: WifiOff,
      title: 'Kiểm tra kết nối mạng',
      description: 'Đảm bảo Internet ổn định và dịch vụ API đang hoạt động, sau đó thử dịch lại.',
    }
  }

  return {
    icon: Cloud,
    title: 'Kiểm tra cấu hình dịch vụ',
    description: 'Kiểm tra API key, quota, model đã chọn và trạng thái dịch vụ rồi thử lại.',
  }
}

export function ApiErrorDialog({
  open,
  provider,
  code,
  message,
  onClose,
}: ApiErrorDialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null)
  const closeButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!open) return

    const previousFocus = document.activeElement as HTMLElement | null
    window.setTimeout(() => closeButtonRef.current?.focus(), 0)
    return () => previousFocus?.focus()
  }, [open])

  if (!open) return null

  const isVoiceError = code.startsWith('FPT_')
  const guidance = getGuidance(code)
  const GuidanceIcon = guidance.icon
  const handleDialogKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
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

  return (
    <div
      className="api-error-dialog-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div
        ref={dialogRef}
        className="api-error-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="api-error-dialog-title"
        aria-describedby="api-error-dialog-description"
        onKeyDown={handleDialogKeyDown}
      >
        <header className="api-error-dialog__header">
          <div>
            <span className="api-error-dialog__eyebrow"><Cloud size={14} /> Kết nối AI cloud</span>
            <h2 id="api-error-dialog-title">Không thể gọi API {provider}</h2>
            <p id="api-error-dialog-description">
              {isVoiceError ? 'Yêu cầu tạo giọng chưa được hoàn tất.' : 'Yêu cầu dịch chưa được hoàn tất.'}
            </p>
          </div>
          <button
            ref={closeButtonRef}
            type="button"
            className="api-error-dialog__close"
            aria-label="Đóng thông báo lỗi API"
            onClick={onClose}
          >
            <X size={18} />
          </button>
        </header>

        <div className="api-error-dialog__body">
          <div className="api-error-dialog__alert">
            <span className="api-error-dialog__alert-icon"><TriangleAlert size={22} /></span>
            <div>
              <strong>Đã xảy ra lỗi khi kết nối dịch vụ</strong>
              <p>{message}</p>
            </div>
          </div>

          <div className="api-error-dialog__guidance">
            <div className="api-error-dialog__guidance-icon"><GuidanceIcon size={17} /></div>
            <div>
              <strong>{guidance.title}</strong>
              <p>{guidance.description}</p>
            </div>
          </div>

          <p className="api-error-dialog__hint">
            {isVoiceError
              ? 'Các cue audio đã hoàn tất vẫn được giữ trong project. Khi thử lại, hệ thống chỉ tiếp tục những cue còn thiếu.'
              : 'Dữ liệu phụ đề hiện có vẫn được giữ nguyên trong project. Bạn có thể đóng thông báo và thử lại sau khi kiểm tra.'}
          </p>
        </div>

        <footer className="api-error-dialog__footer">
          <span className="api-error-dialog__code">Mã lỗi: {code}</span>
          <button type="button" className="api-error-dialog__confirm" onClick={onClose}>
            Đã hiểu
          </button>
        </footer>
      </div>
    </div>
  )
}
