import { useState } from 'react'
import {
  AudioWaveform,
  Captions,
  Languages,
  ScanLine,
  Sparkles,
  WandSparkles,
} from 'lucide-react'
import { SectionCard, SegmentTab, SelectField, Toggle } from './Ui'

export function SettingsPanel() {
  const [mode, setMode] = useState<'subtitle' | 'dubbing'>('subtitle')
  const [speechCollapsed, setSpeechCollapsed] = useState(false)
  const [translationCollapsed, setTranslationCollapsed] = useState(false)
  const [ocrEnabled, setOcrEnabled] = useState(false)
  const [contextEnabled, setContextEnabled] = useState(true)

  return (
    <aside className="panel settings-panel" aria-label="Thiết lập xử lý">
      <div className="panel-mode-tabs" role="tablist" aria-label="Chế độ biên tập">
        <SegmentTab
          active={mode === 'subtitle'}
          icon={<Captions size={16} />}
          label="Phụ đề"
          onClick={() => setMode('subtitle')}
        />
        <SegmentTab
          active={mode === 'dubbing'}
          icon={<AudioWaveform size={16} />}
          label="Thuyết minh"
          onClick={() => setMode('dubbing')}
        />
      </div>

      <div className="panel-scroll">
        <SectionCard
          title="NHẬN DẠNG GIỌNG NÓI"
          icon={<AudioWaveform size={16} />}
          collapsed={speechCollapsed}
          onToggle={() => setSpeechCollapsed((value) => !value)}
          badge="BƯỚC 1"
        >
          <SelectField label="NGÔN NGỮ GỐC" defaultValue="auto">
            <option value="auto">Tự động phát hiện</option>
            <option value="en">Tiếng Anh</option>
            <option value="zh">Tiếng Trung</option>
            <option value="ja">Tiếng Nhật</option>
            <option value="ko">Tiếng Hàn</option>
          </SelectField>

          <SelectField
            label="MÔ HÌNH NHẬN DẠNG"
            defaultValue="balanced"
            helper="Cân bằng tốc độ và độ chính xác"
          >
            <option value="balanced">Whisper Balanced · Khuyến nghị</option>
            <option value="fast">Whisper Fast · Máy yếu</option>
            <option value="accurate">Whisper Accurate · Chất lượng cao</option>
          </SelectField>

          <Toggle
            checked={ocrEnabled}
            label="Hiện vùng OCR"
            description="Dùng khi video có phụ đề cứng"
            icon={<ScanLine size={17} />}
            onChange={setOcrEnabled}
          />
        </SectionCard>

        <SectionCard
          title="DỊCH SANG TIẾNG VIỆT"
          icon={<Languages size={16} />}
          collapsed={translationCollapsed}
          onToggle={() => setTranslationCollapsed((value) => !value)}
          badge="BƯỚC 2"
        >
          <SelectField label="VĂN PHONG" defaultValue="natural">
            <option value="natural">Tự nhiên, dễ nghe</option>
            <option value="formal">Trang trọng</option>
            <option value="concise">Ngắn gọn theo thời lượng</option>
          </SelectField>

          <Toggle
            checked={contextEnabled}
            label="Dịch theo ngữ cảnh"
            description="Giữ thuật ngữ và cách xưng hô nhất quán"
            icon={<Sparkles size={17} />}
            onChange={setContextEnabled}
          />

          <button type="button" className="secondary-action">
            <WandSparkles size={16} />
            <span>Thiết lập glossary</span>
          </button>
        </SectionCard>

        <div className="panel-tip">
          <Sparkles size={16} />
          <div>
            <strong>Mẹo chất lượng</strong>
            <p>Duyệt transcript trước khi tạo giọng để hạn chế sai tên riêng.</p>
          </div>
        </div>
      </div>

      <footer className="panel-footer">
        <span>UI Preview</span>
        <span>V1.0.0</span>
      </footer>
    </aside>
  )
}
