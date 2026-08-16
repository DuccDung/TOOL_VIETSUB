import type { SubtitleSegment } from '../types'

export const demoSegments: SubtitleSegment[] = [
  {
    cueId: 'c9e1ef27-c12e-4b2c-a420-0084c90f3181',
    id: 1,
    start: 0.8,
    end: 4.4,
    original: 'Welcome back to our channel.',
    translated: 'Chào mừng bạn quay trở lại với kênh của chúng tôi.',
    status: 'translated',
  },
  {
    cueId: 'd782e6ad-24e4-4c4d-ac7a-1015691068fd',
    id: 2,
    start: 5.1,
    end: 9.7,
    original: 'Today we will explore a completely new workflow.',
    translated: 'Hôm nay chúng ta sẽ khám phá một quy trình hoàn toàn mới.',
    status: 'review',
  },
  {
    cueId: 'c85a882c-0456-43ed-971c-808e55ce0c4a',
    id: 3,
    start: 10.4,
    end: 14.9,
    original: 'Let us get started with the first step.',
    translated: 'Hãy bắt đầu với bước đầu tiên.',
    status: 'missing-audio',
  },
]
