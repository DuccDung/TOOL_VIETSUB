import type { SubtitleStyleSettings } from '../types'

export type SubtitleStylePreset = {
  id: Exclude<SubtitleStyleSettings['presetId'], 'custom'>
  name: string
  description: string
  style: SubtitleStyleSettings
}

export const defaultSubtitleStyle: SubtitleStyleSettings = {
  presetId: 'readable',
  fontFamily: 'Arial',
  fontSizePercent: 4.2,
  bold: true,
  textColor: '#FFFFFF',
  outlineColor: '#000000',
  outlineSize: 1.2,
  shadowSize: 0,
  backgroundMode: 'box',
  backgroundColor: '#020617',
  backgroundOpacity: 68,
  horizontalAlignment: 'center',
  verticalPosition: 'bottom',
  positionXPercent: 50,
  positionYPercent: 94,
  maxWidthPercent: 90,
  maxLines: 2,
}

const preset = (
  id: SubtitleStylePreset['id'],
  name: string,
  description: string,
  overrides: Partial<SubtitleStyleSettings>,
): SubtitleStylePreset => ({
  id,
  name,
  description,
  style: { ...defaultSubtitleStyle, ...overrides, presetId: id },
})

export const subtitleStylePresets: SubtitleStylePreset[] = [
  preset('readable', 'Dễ đọc', 'Nền tối, chữ trắng', {}),
  preset('outline', 'Viền đen', 'Không nền, viền rõ', {
    backgroundMode: 'none',
    outlineSize: 2.2,
  }),
  preset('tiktok', 'TikTok', 'Chữ lớn và đậm', {
    fontSizePercent: 5.2,
    backgroundMode: 'none',
    outlineSize: 3.4,
    maxWidthPercent: 86,
  }),
  preset('cinematic', 'Điện ảnh', 'Nhỏ, thanh lịch', {
    fontFamily: 'Times New Roman',
    fontSizePercent: 3.4,
    bold: false,
    backgroundMode: 'none',
    outlineSize: 1,
    shadowSize: 1.2,
    positionYPercent: 92,
  }),
  preset('yellow', 'Vàng nổi bật', 'Vàng với viền đen', {
    fontFamily: 'Tahoma',
    textColor: '#FDE047',
    backgroundMode: 'none',
    outlineSize: 2.4,
  }),
  preset('minimal', 'Tối giản', 'Nhẹ và ít che hình', {
    fontFamily: 'Segoe UI',
    fontSizePercent: 3.7,
    bold: false,
    backgroundMode: 'none',
    outlineSize: 0.8,
    shadowSize: 1,
  }),
]

export function applySubtitlePreset(id: SubtitleStylePreset['id']): SubtitleStyleSettings {
  const selected = subtitleStylePresets.find((item) => item.id === id)
  return { ...(selected?.style ?? defaultSubtitleStyle) }
}

export function normalizeSubtitleText(text: string): string {
  return text.trim().replace(/\s+/gu, ' ')
}
