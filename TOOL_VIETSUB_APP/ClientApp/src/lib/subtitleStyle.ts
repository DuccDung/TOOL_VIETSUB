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

export function estimateSubtitleLineCapacity(style: SubtitleStyleSettings): number {
  return Math.min(60, Math.max(12, Math.round(
    32 * (style.maxWidthPercent / 90) * (4.2 / style.fontSizePercent),
  )))
}

export function wrapSubtitleText(text: string, maxLines: number, lineCapacity = 32): string {
  const words = text.trim().split(/\s+/).filter(Boolean)
  if (words.length < 2 || maxLines <= 1) return words.join(' ')

  const totalLength = words.join(' ').length
  const estimatedLines = Math.max(1, Math.ceil(totalLength / Math.max(8, lineCapacity)))
  const lineCount = Math.min(Math.max(1, Math.round(maxLines)), estimatedLines, words.length)
  const remainingLength = () => words.slice(wordIndex).reduce((sum, word) => sum + word.length, 0)
  const lines: string[] = []
  let wordIndex = 0

  for (let lineIndex = 0; lineIndex < lineCount; lineIndex += 1) {
    const linesLeft = lineCount - lineIndex
    if (linesLeft === 1) {
      lines.push(words.slice(wordIndex).join(' '))
      break
    }

    const target = (remainingLength() + (words.length - wordIndex - 1)) / linesLeft
    const current: string[] = []
    let length = 0
    while (wordIndex < words.length - (linesLeft - 1)) {
      const word = words[wordIndex]
      const nextLength = length + (current.length ? 1 : 0) + word.length
      if (current.length && nextLength > target) break
      current.push(word)
      length = nextLength
      wordIndex += 1
    }
    lines.push(current.join(' '))
  }

  return lines.filter(Boolean).join('\n')
}
