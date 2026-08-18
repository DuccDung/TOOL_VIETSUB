import { useEffect, useState } from 'react'
import { BookOpenText, Save, ServerCog, ShieldCheck, Users } from 'lucide-react'
import type { TranslationSettingsInfo } from '../types'
import { SelectField, Toggle } from './Ui'

type TranslationSettingsEditorProps = {
  settings: TranslationSettingsInfo
  sourceLanguageCode: string
  subtitleCount: number
  disabled?: boolean
  onSave: (
    settings: TranslationSettingsInfo,
    apiKey?: string,
    clearApiKey?: boolean,
  ) => void
}

function defaultModel(
  provider: TranslationSettingsInfo['provider'],
  qualityMode: TranslationSettingsInfo['qualityMode'],
  sourceLanguageCode: string,
) {
  if (provider === 'local') {
    return sourceLanguageCode === 'en' ? 'argos-en-vi' : 'opus-mt-zh-vi-official-v2'
  }
  if (provider === 'openai') {
    if (qualityMode === 'fast') return 'gpt-5.6-luna'
    if (qualityMode === 'high') return 'gpt-5.6-sol'
    return 'gpt-5.6-terra'
  }
  if (provider === 'deepseek') {
    return qualityMode === 'high' ? 'deepseek-v4-pro' : 'deepseek-v4-flash'
  }
  if (provider === 'groq') {
    return qualityMode === 'fast' ? 'openai/gpt-oss-20b' : 'openai/gpt-oss-120b'
  }
  return qualityMode === 'high' ? 'gemini-3.1-pro-preview' : 'gemini-3.6-flash'
}

export function TranslationSettingsEditor({
  settings,
  sourceLanguageCode,
  subtitleCount,
  disabled = false,
  onSave,
}: TranslationSettingsEditorProps) {
  const [draft, setDraft] = useState(settings)
  const estimatedSceneSize = draft.provider === 'groq' ? 8 : 12
  const estimatedApiCalls = Math.ceil(subtitleCount / estimatedSceneSize) * (draft.reviewEnabled ? 2 : 1)

  useEffect(() => {
    setDraft(settings)
  }, [settings])

  const update = (patch: Partial<TranslationSettingsInfo>) => {
    setDraft((current) => ({ ...current, ...patch }))
  }

  const chooseProvider = (provider: TranslationSettingsInfo['provider']) => {
    update({
      provider,
      modelId: defaultModel(provider, draft.qualityMode, sourceLanguageCode),
      reviewEnabled: provider === 'groq' ? draft.qualityMode !== 'fast' : draft.reviewEnabled,
    })
  }

  const chooseQuality = (qualityMode: TranslationSettingsInfo['qualityMode']) => {
    update({
      qualityMode,
      modelId: defaultModel(draft.provider, qualityMode, sourceLanguageCode),
      reviewEnabled: draft.provider === 'groq' ? qualityMode !== 'fast' : draft.reviewEnabled,
    })
  }

  const save = () => {
    onSave(draft, undefined, false)
  }

  return (
    <div className="translation-settings">
      <SelectField
        label="NHÀ CUNG CẤP"
        value={draft.provider}
        disabled={disabled}
        helper={draft.provider === 'local'
          ? 'Chạy hoàn toàn trên máy, phù hợp chế độ riêng tư.'
          : draft.provider === 'groq'
            ? 'Dùng Groq Free Tier; chỉ văn bản phụ đề và ngữ cảnh được gửi đi.'
            : 'Chỉ văn bản phụ đề và ngữ cảnh được gửi tới nhà cung cấp.'}
        onChange={(event) => chooseProvider(event.target.value as TranslationSettingsInfo['provider'])}
      >
        <option value="local">Local · Riêng tư</option>
        <option value="openai">OpenAI · Dịch có ngữ cảnh</option>
        <option value="gemini">Gemini · Ngữ cảnh dài</option>
        <option value="deepseek">DeepSeek · Chi phí tối ưu</option>
        <option value="groq">Groq · Free Tier tốc độ cao</option>
      </SelectField>

      <SelectField
        label="CHẾ ĐỘ CHẤT LƯỢNG"
        value={draft.qualityMode}
        disabled={disabled || draft.provider === 'local'}
        onChange={(event) => chooseQuality(event.target.value as TranslationSettingsInfo['qualityMode'])}
      >
        <option value="fast">Nhanh · Chi phí thấp</option>
        <option value="balanced">Cân bằng · Khuyến nghị</option>
        <option value="high">Chất lượng cao</option>
      </SelectField>

      <label className="field-group">
        <span className="field-label">MODEL</span>
        <input
          className="translation-settings__input"
          value={draft.modelId}
          disabled={disabled || draft.provider === 'local'}
          maxLength={120}
          spellCheck={false}
          onChange={(event) => update({ modelId: event.target.value })}
        />
        <span className="field-helper">
          {draft.provider === 'groq'
            ? 'GPT-OSS dùng JSON Schema nghiêm ngặt; model Groq khác dùng JSON Object Mode.'
            : 'Có thể thay model mà không cần đổi pipeline.'}
        </span>
      </label>

      {draft.provider !== 'local' ? (
        <div className="translation-managed-key">
          <span><ServerCog size={17} /></span>
          <div>
            <strong>API key do Server quản lý</strong>
            <small>App chỉ nhận key vào RAM khi bắt đầu mỗi lượt Cloud, sau đó gọi thẳng nhà cung cấp.</small>
          </div>
          <em><ShieldCheck size={12} /> Không lưu local</em>
        </div>
      ) : null}

      <Toggle
        checked={draft.reviewEnabled}
        disabled={disabled || draft.provider === 'local'}
        label="Kiểm duyệt lượt hai"
        description="Sửa sai nghĩa, xưng hô, thuật ngữ và câu quá dài."
        icon={<BookOpenText size={16} />}
        onChange={(reviewEnabled) => update({ reviewEnabled })}
      />

      <Toggle
        checked={draft.fallbackToLocal}
        disabled={disabled || draft.provider === 'local'}
        label="Dự phòng bằng model local"
        description="Tiếp tục khi cloud lỗi tạm thời; có thể cần tải model local."
        icon={<ShieldCheck size={16} />}
        onChange={(fallbackToLocal) => update({ fallbackToLocal })}
      />

      {draft.provider !== 'local' && subtitleCount > 0 ? (
        <span className="field-helper">
          Ước tính tối thiểu {estimatedApiCalls} lượt API cho {subtitleCount} cue; số thực tế phụ thuộc ranh giới cảnh.
        </span>
      ) : null}

      <label className="field-group">
        <span className="field-label">BỐI CẢNH VIDEO</span>
        <textarea
          className="translation-settings__textarea"
          value={draft.projectContext}
          disabled={disabled}
          maxLength={4000}
          rows={3}
          placeholder="Chủ đề, tóm tắt nội dung, thời đại, đối tượng người xem..."
          onChange={(event) => update({ projectContext: event.target.value })}
        />
      </label>

      <label className="field-group">
        <span className="field-label"><Users size={13} /> NHÂN VẬT VÀ XƯNG HÔ</span>
        <textarea
          className="translation-settings__textarea"
          value={draft.characterInstructions}
          disabled={disabled}
          maxLength={4000}
          rows={3}
          placeholder="speaker_1 là Minh; Minh gọi Lan là chị, xưng em..."
          onChange={(event) => update({ characterInstructions: event.target.value })}
        />
      </label>

      <label className="field-group">
        <span className="field-label">PHONG CÁCH DỊCH</span>
        <textarea
          className="translation-settings__textarea"
          value={draft.styleInstructions}
          disabled={disabled}
          maxLength={2000}
          rows={2}
          placeholder="Tự nhiên, ngắn gọn, phù hợp hội thoại..."
          onChange={(event) => update({ styleInstructions: event.target.value })}
        />
      </label>

      <label className="field-group">
        <span className="field-label">GLOSSARY</span>
        <textarea
          className="translation-settings__textarea translation-settings__textarea--glossary"
          value={draft.glossaryText}
          disabled={disabled}
          maxLength={20000}
          rows={4}
          spellCheck={false}
          placeholder={'Xiaomi = Xiaomi | tên thương hiệu\nMaster = Sư phụ | cách xưng hô'}
          onChange={(event) => update({ glossaryText: event.target.value })}
        />
        <span className="field-helper">
          Mỗi dòng: từ gốc = tiếng Việt | ghi chú. Bộ nhớ dịch đã có {draft.translationMemoryCount} mục.
        </span>
      </label>

      <div className="translation-settings__actions">
        <button type="button" onClick={save} disabled={disabled || !draft.modelId.trim()}>
          <Save size={14} />
          Lưu cấu hình
        </button>
      </div>
    </div>
  )
}
