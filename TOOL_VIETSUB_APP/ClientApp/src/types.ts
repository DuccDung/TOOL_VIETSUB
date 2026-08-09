export type VideoInfo = {
  fileName: string
  extension: string
  sizeBytes: number
  durationSeconds: number
}

export type SubtitleSegment = {
  id: number
  start: number
  end: number
  original: string
  translated: string
  status: 'translated' | 'review' | 'missing-audio'
}

export type ToastMessage = {
  id: number
  title: string
  description: string
  tone?: 'info' | 'success' | 'warning'
}
