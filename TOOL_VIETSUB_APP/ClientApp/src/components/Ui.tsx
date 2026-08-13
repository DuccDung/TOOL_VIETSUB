import type { ButtonHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react'
import { Check, ChevronDown } from 'lucide-react'

type IconButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string
  active?: boolean
  size?: 'small' | 'medium'
  children: ReactNode
}

export function IconButton({
  label,
  active = false,
  size = 'medium',
  children,
  className = '',
  ...props
}: IconButtonProps) {
  return (
    <button
      type="button"
      className={`icon-button icon-button--${size} ${active ? 'is-active' : ''} ${className}`}
      aria-label={label}
      title={label}
      {...props}
    >
      {children}
    </button>
  )
}

type SegmentTabProps = {
  active?: boolean
  icon?: ReactNode
  label: string
  onClick?: () => void
}

export function SegmentTab({ active, icon, label, onClick }: SegmentTabProps) {
  return (
    <button
      type="button"
      role="tab"
      className={`segment-tab ${active ? 'is-active' : ''}`}
      aria-selected={active}
      onClick={onClick}
    >
      {icon}
      <span>{label}</span>
    </button>
  )
}

type SelectFieldProps = SelectHTMLAttributes<HTMLSelectElement> & {
  label: string
  helper?: string
}

export function SelectField({
  label,
  helper,
  children,
  ...props
}: SelectFieldProps) {
  return (
    <label className="field-group">
      <span className="field-label">{label}</span>
      <span className="select-shell">
        <select {...props}>{children}</select>
        <ChevronDown size={15} aria-hidden="true" />
      </span>
      {helper ? <span className="field-helper">{helper}</span> : null}
    </label>
  )
}

type ToggleProps = {
  checked: boolean
  label: string
  description?: string
  icon?: ReactNode
  disabled?: boolean
  onChange: (checked: boolean) => void
}

export function Toggle({
  checked,
  label,
  description,
  icon,
  disabled = false,
  onChange,
}: ToggleProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className="toggle-row"
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span className="toggle-copy">
        {icon ? <span className="toggle-icon">{icon}</span> : null}
        <span>
          <strong>{label}</strong>
          {description ? <small>{description}</small> : null}
        </span>
      </span>
      <span className={`switch ${checked ? 'is-checked' : ''}`} aria-hidden="true">
        <span className="switch-thumb">{checked ? <Check size={10} /> : null}</span>
      </span>
    </button>
  )
}

type SectionCardProps = {
  title: string
  icon: ReactNode
  collapsed: boolean
  onToggle: () => void
  children: ReactNode
  badge?: string
}

export function SectionCard({
  title,
  icon,
  collapsed,
  onToggle,
  children,
  badge,
}: SectionCardProps) {
  return (
    <section className={`section-card ${collapsed ? 'is-collapsed' : ''}`}>
      <button
        type="button"
        className="section-card__header"
        onClick={onToggle}
        aria-expanded={!collapsed}
      >
        <span className="section-card__title">
          {icon}
          <span>{title}</span>
          {badge ? <span className="mini-badge">{badge}</span> : null}
        </span>
        <ChevronDown size={16} className="section-card__chevron" aria-hidden="true" />
      </button>
      {!collapsed ? <div className="section-card__body">{children}</div> : null}
    </section>
  )
}

type RangeProps = {
  label: string
  value: number
  min?: number
  max?: number
  step?: number
  suffix?: string
  icon: ReactNode
  disabled?: boolean
  onChange: (value: number) => void
}

export function CompactRange({
  label,
  value,
  min = 0,
  max = 100,
  step = 1,
  suffix = '',
  icon,
  disabled = false,
  onChange,
}: RangeProps) {
  const progress = ((value - min) / (max - min)) * 100

  return (
    <label className="compact-range" title={label}>
      <span className="compact-range__icon">{icon}</span>
      <input
        aria-label={label}
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        style={{ '--range-progress': `${progress}%` } as React.CSSProperties}
        onChange={(event) => onChange(Number(event.target.value))}
      />
      <output>{value.toFixed(step < 1 ? 1 : 0)}{suffix}</output>
    </label>
  )
}
