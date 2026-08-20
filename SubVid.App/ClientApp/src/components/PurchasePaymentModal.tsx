import { Check, Clock3, Copy, Landmark, LoaderCircle, TriangleAlert, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import {
  formatVietnamDateTime,
  isPurchasePollingFinal,
  remainingSecondsUntilUtc,
  shouldPollPurchase,
} from '../lib/purchasePolling'
import type { PurchaseCheckoutInfo } from '../types'

type PurchasePaymentModalProps = {
  checkout: PurchaseCheckoutInfo
  pollPending: boolean
  error: string | null
  onPoll: () => void
  onClose: () => void
}

function formatMoney(amount: number, currency: string) {
  return new Intl.NumberFormat('vi-VN', {
    style: 'currency',
    currency,
    maximumFractionDigits: 0,
  }).format(amount)
}

export function PurchasePaymentModal({
  checkout,
  pollPending,
  error,
  onPoll,
  onClose,
}: PurchasePaymentModalProps) {
  const [remainingSeconds, setRemainingSeconds] = useState(() => remainingSecondsUntilUtc(checkout.expiresAtUtc))
  const [copied, setCopied] = useState<string | null>(null)
  const [qrFailed, setQrFailed] = useState(false)
  const expired = checkout.isExpired || remainingSeconds <= 0
  const final = isPurchasePollingFinal(checkout.isPaid, checkout.isExpired, remainingSeconds)

  useEffect(() => {
    setRemainingSeconds(remainingSecondsUntilUtc(checkout.expiresAtUtc))
  }, [checkout.expiresAtUtc])

  useEffect(() => {
    if (final) return
    const countdown = window.setInterval(() => {
      setRemainingSeconds(remainingSecondsUntilUtc(checkout.expiresAtUtc))
    }, 1000)
    return () => window.clearInterval(countdown)
  }, [checkout.expiresAtUtc, final])

  useEffect(() => {
    if (final) return
    const polling = window.setInterval(() => {
      if (shouldPollPurchase(final, pollPending)) onPoll()
    }, 5000)
    return () => window.clearInterval(polling)
  }, [final, onPoll, pollPending])

  const countdownText = useMemo(() => {
    const minutes = Math.floor(remainingSeconds / 60).toString().padStart(2, '0')
    const seconds = (remainingSeconds % 60).toString().padStart(2, '0')
    return `${minutes}:${seconds}`
  }, [remainingSeconds])

  const copy = async (label: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(label)
      window.setTimeout(() => setCopied(null), 1500)
    } catch {
      setCopied(null)
    }
  }

  const rows = [
    ['Số tài khoản', checkout.accountNumber],
    ['Chủ tài khoản', checkout.accountName],
    ['Nội dung', checkout.transferContent],
  ] as const

  return (
    <div className="purchase-modal" role="dialog" aria-modal="true" aria-labelledby="purchase-modal-title">
      <button type="button" className="purchase-modal__backdrop" aria-label="Đóng" onClick={onClose} />
      <section className={`purchase-modal__surface ${checkout.isPaid ? 'is-paid' : expired ? 'is-expired' : ''}`}>
        <header>
          <div>
            <span>THANH TOÁN SEPAY</span>
            <h2 id="purchase-modal-title">{checkout.planName}</h2>
            <p>{checkout.message}</p>
          </div>
          <button type="button" className="purchase-modal__close" onClick={onClose} aria-label="Đóng"><X size={19} /></button>
        </header>

        <div className="purchase-modal__body">
          <div className="purchase-modal__qr-column">
            <div className="purchase-modal__qr">
              {checkout.isPaid ? (
                <div className="purchase-modal__result is-success"><Check size={48} /><strong>Đã thanh toán</strong></div>
              ) : expired ? (
                <div className="purchase-modal__result is-warning"><TriangleAlert size={44} /><strong>QR đã hết hạn</strong></div>
              ) : !qrFailed ? (
                <img src={checkout.qrImageUrl} alt={`QR thanh toán ${checkout.transactionCode}`} onError={() => setQrFailed(true)} />
              ) : (
                <div className="purchase-modal__result is-warning"><TriangleAlert size={38} /><strong>Không tải được QR</strong><small>Hãy chuyển khoản thủ công bằng thông tin bên cạnh.</small></div>
              )}
            </div>
            <div className="purchase-modal__timer">
              {pollPending ? <LoaderCircle className="is-spinning" size={16} /> : <Clock3 size={16} />}
              <span>
                <strong>{checkout.isPaid ? 'Hoàn tất' : expired ? 'Đã hết hạn' : `Còn ${countdownText}`}</strong>
                <small>Hết hạn {formatVietnamDateTime(checkout.expiresAtUtc)} · giờ Việt Nam</small>
              </span>
            </div>
          </div>

          <div className="purchase-modal__details">
            <div className="purchase-modal__bank">
              <Landmark size={20} />
              <div><small>Ngân hàng nhận</small><strong>{checkout.bankName} · {checkout.bankShortName}</strong></div>
            </div>
            {rows.map(([label, value]) => (
              <div className="purchase-modal__row" key={label}>
                <span>{label}</span>
                <strong>{value}</strong>
                <button type="button" onClick={() => copy(label, value)} aria-label={`Sao chép ${label}`}>
                  {copied === label ? <Check size={15} /> : <Copy size={15} />}
                </button>
              </div>
            ))}
            <div className="purchase-modal__amount">
              <span>Số tiền chính xác</span>
              <strong>{formatMoney(checkout.amount, checkout.currency)}</strong>
              <button type="button" onClick={() => copy('Số tiền', checkout.amount.toFixed(0))}>
                {copied === 'Số tiền' ? <Check size={15} /> : <Copy size={15} />} Sao chép
              </button>
            </div>
            <small className="purchase-modal__order">Đơn {checkout.orderNumber} · Mã {checkout.transactionCode}</small>
            {error ? <p className="purchase-modal__error">{error}</p> : null}
          </div>
        </div>
      </section>
    </div>
  )
}
