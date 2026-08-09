import type { SubtitleSegment } from '../types'

export const demoSegments: SubtitleSegment[] = [
  {
    id: 1,
    start: 0.8,
    end: 4.4,
    original: 'Welcome back to our channel.',
    translated: 'Chào mừng bạn quay trở lại với kênh của chúng tôi.',
    status: 'translated',
  },
  {
    id: 2,
    start: 5.1,
    end: 9.7,
    original: 'Today we will explore a completely new workflow.',
    translated: 'Hôm nay chúng ta sẽ khám phá một quy trình hoàn toàn mới.',
    status: 'review',
  },
  {
    id: 3,
    start: 10.4,
    end: 14.9,
    original: 'Let us get started with the first step.',
    translated: 'Hãy bắt đầu với bước đầu tiên.',
    status: 'missing-audio',
  },
]
