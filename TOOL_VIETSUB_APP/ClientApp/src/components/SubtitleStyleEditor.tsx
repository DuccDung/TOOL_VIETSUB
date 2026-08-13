import {
  AlignCenter,
  AlignLeft,
  AlignRight,
  CaseUpper,
  Frame,
  MoveVertical,
  Rows3,
  Type,
} from 'lucide-react'
import { applySubtitlePreset, subtitleStylePresets } from '../lib/subtitleStyle'
import type { SubtitleStyleSettings } from '../types'
import { CompactRange, SelectField } from './Ui'

type SubtitleStyleEditorProps = {
  style: SubtitleStyleSettings
  disabled?: boolean
  onChange: (style: SubtitleStyleSettings) => void
}

const withAlpha = (hex: string, opacity: number) => {
  const value = hex.replace('#', '')
  if (!/^[0-9a-f]{6}$/i.test(value)) return 'rgba(2, 6, 23, 0.68)'
  const red = Number.parseInt(value.slice(0, 2), 16)
  const green = Number.parseInt(value.slice(2, 4), 16)
  const blue = Number.parseInt(value.slice(4, 6), 16)
  return `rgba(${red}, ${green}, ${blue}, ${opacity / 100})`
}

export function SubtitleStyleEditor({
  style,
  disabled = false,
  onChange,
}: SubtitleStyleEditorProps) {
  const update = (patch: Partial<SubtitleStyleSettings>) => onChange({
    ...style,
    ...patch,
    presetId: patch.presetId ?? 'custom',
  })

  const chooseHorizontalAlignment = (horizontalAlignment: SubtitleStyleSettings['horizontalAlignment']) => {
    const positionXPercent = horizontalAlignment === 'left' ? 5 : horizontalAlignment === 'right' ? 95 : 50
    update({ horizontalAlignment, positionXPercent })
  }

  const chooseVerticalPosition = (verticalPosition: Exclude<SubtitleStyleSettings['verticalPosition'], 'custom'>) => {
    const positionYPercent = verticalPosition === 'top' ? 6 : verticalPosition === 'middle' ? 50 : 94
    update({ verticalPosition, positionYPercent })
  }

  const contrastWarning = style.backgroundMode === 'none' && style.outlineSize < 0.8

  return (
    <div className="subtitle-style-editor">
      <div className="subtitle-style-presets" role="list" aria-label="Mẫu kiểu phụ đề">
        {subtitleStylePresets.map((preset) => (
          <button
            key={preset.id}
            type="button"
            role="listitem"
            className={style.presetId === preset.id ? 'is-active' : ''}
            disabled={disabled}
            title={preset.description}
            onClick={() => onChange(applySubtitlePreset(preset.id))}
          >
            <span
              className="subtitle-style-presets__sample"
              style={{
                color: preset.style.textColor,
                background: preset.style.backgroundMode === 'box'
                  ? withAlpha(preset.style.backgroundColor, preset.style.backgroundOpacity)
                  : 'transparent',
                fontFamily: preset.style.fontFamily,
                fontWeight: preset.style.bold ? 700 : 400,
                WebkitTextStroke: `${Math.max(0, preset.style.outlineSize * 0.18)}px ${preset.style.outlineColor}`,
              }}
            >
              Aa
            </span>
            <strong>{preset.name}</strong>
          </button>
        ))}
      </div>

      <SelectField
        label="FONT CHỮ"
        value={style.fontFamily}
        disabled={disabled}
        helper="Danh sách font tương thích cả preview và FFmpeg"
        onChange={(event) => update({ fontFamily: event.target.value as SubtitleStyleSettings['fontFamily'] })}
      >
        <option value="Arial">Arial</option>
        <option value="Segoe UI">Segoe UI</option>
        <option value="Tahoma">Tahoma</option>
        <option value="Verdana">Verdana</option>
        <option value="Times New Roman">Times New Roman</option>
      </SelectField>

      <div className="subtitle-style-range-grid">
        <CompactRange
          label="Cỡ chữ theo chiều cao video"
          value={style.fontSizePercent}
          min={1.5}
          max={10}
          step={0.1}
          suffix="%"
          icon={<Type size={14} />}
          disabled={disabled}
          onChange={(fontSizePercent) => update({ fontSizePercent })}
        />
        <CompactRange
          label="Độ dày viền"
          value={style.outlineSize}
          min={0}
          max={8}
          step={0.1}
          icon={<Frame size={14} />}
          disabled={disabled}
          onChange={(outlineSize) => update({ outlineSize })}
        />
        <CompactRange
          label="Độ đổ bóng"
          value={style.shadowSize}
          min={0}
          max={8}
          step={0.1}
          icon={<CaseUpper size={14} />}
          disabled={disabled}
          onChange={(shadowSize) => update({ shadowSize })}
        />
        <CompactRange
          label="Chiều rộng tối đa"
          value={style.maxWidthPercent}
          min={35}
          max={100}
          step={1}
          suffix="%"
          icon={<MoveVertical size={14} />}
          disabled={disabled}
          onChange={(maxWidthPercent) => update({ maxWidthPercent })}
        />
        <CompactRange
          label="Số dòng mục tiêu"
          value={style.maxLines}
          min={1}
          max={3}
          step={1}
          icon={<Rows3 size={14} />}
          disabled={disabled}
          onChange={(maxLines) => update({ maxLines: Math.round(maxLines) as 1 | 2 | 3 })}
        />
      </div>

      <div className="subtitle-style-choice-row">
        <span className="field-label">ĐỘ ĐẬM</span>
        <div className="subtitle-style-segmented">
          <button type="button" className={!style.bold ? 'is-active' : ''} disabled={disabled} onClick={() => update({ bold: false })}>Thường</button>
          <button type="button" className={style.bold ? 'is-active' : ''} disabled={disabled} onClick={() => update({ bold: true })}>Đậm</button>
        </div>
      </div>

      <div className="subtitle-style-colors">
        <label>
          <span>Màu chữ</span>
          <span><input type="color" value={style.textColor} disabled={disabled} onChange={(event) => update({ textColor: event.target.value.toUpperCase() })} /><code>{style.textColor}</code></span>
        </label>
        <label>
          <span>Màu viền</span>
          <span><input type="color" value={style.outlineColor} disabled={disabled} onChange={(event) => update({ outlineColor: event.target.value.toUpperCase() })} /><code>{style.outlineColor}</code></span>
        </label>
      </div>

      <div className="subtitle-style-choice-row">
        <span className="field-label">NỀN PHỤ ĐỀ</span>
        <div className="subtitle-style-segmented">
          <button type="button" className={style.backgroundMode === 'none' ? 'is-active' : ''} disabled={disabled} onClick={() => update({ backgroundMode: 'none' })}>Không nền</button>
          <button type="button" className={style.backgroundMode === 'box' ? 'is-active' : ''} disabled={disabled} onClick={() => update({ backgroundMode: 'box' })}>Nền hộp</button>
        </div>
      </div>

      {style.backgroundMode === 'box' ? (
        <div className="subtitle-style-background-row">
          <label>
            <span className="field-label">MÀU NỀN</span>
            <input type="color" value={style.backgroundColor} disabled={disabled} onChange={(event) => update({ backgroundColor: event.target.value.toUpperCase() })} />
          </label>
          <CompactRange
            label="Độ hiển thị nền"
            value={style.backgroundOpacity}
            min={0}
            max={100}
            step={1}
            suffix="%"
            icon={<Frame size={14} />}
            disabled={disabled}
            onChange={(backgroundOpacity) => update({ backgroundOpacity })}
          />
        </div>
      ) : null}

      <div className="subtitle-style-position-grid">
        <div>
          <span className="field-label">CĂN CHỮ</span>
          <div className="subtitle-style-icon-buttons">
            <button type="button" aria-label="Căn trái" className={style.horizontalAlignment === 'left' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseHorizontalAlignment('left')}><AlignLeft size={15} /></button>
            <button type="button" aria-label="Căn giữa" className={style.horizontalAlignment === 'center' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseHorizontalAlignment('center')}><AlignCenter size={15} /></button>
            <button type="button" aria-label="Căn phải" className={style.horizontalAlignment === 'right' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseHorizontalAlignment('right')}><AlignRight size={15} /></button>
          </div>
        </div>
        <div>
          <span className="field-label">VỊ TRÍ</span>
          <div className="subtitle-style-segmented">
            <button type="button" className={style.verticalPosition === 'top' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseVerticalPosition('top')}>Trên</button>
            <button type="button" className={style.verticalPosition === 'middle' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseVerticalPosition('middle')}>Giữa</button>
            <button type="button" className={style.verticalPosition === 'bottom' ? 'is-active' : ''} disabled={disabled} onClick={() => chooseVerticalPosition('bottom')}>Dưới</button>
          </div>
        </div>
      </div>

      {contrastWarning ? (
        <p className="subtitle-style-warning">Viền đang quá mỏng cho kiểu không nền; chữ có thể khó đọc trên cảnh sáng.</p>
      ) : null}

      <div className="subtitle-style-reset">
        <span>X {Math.round(style.positionXPercent)}% · Y {Math.round(style.positionYPercent)}%</span>
        <button type="button" disabled={disabled} onClick={() => onChange(applySubtitlePreset('readable'))}>
          Đặt lại mặc định
        </button>
      </div>
    </div>
  )
}
