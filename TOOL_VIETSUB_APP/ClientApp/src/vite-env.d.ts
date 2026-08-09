/// <reference types="vite/client" />

interface WebViewHost {
  postMessage(message: unknown): void
  addEventListener(
    type: 'message',
    listener: (event: MessageEvent<unknown>) => void,
  ): void
  removeEventListener(
    type: 'message',
    listener: (event: MessageEvent<unknown>) => void,
  ): void
}

interface Window {
  chrome?: {
    webview?: WebViewHost
  }
}
