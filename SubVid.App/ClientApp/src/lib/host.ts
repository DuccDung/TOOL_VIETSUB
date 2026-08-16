type HostEnvelope = Record<string, unknown> & { type: string }

export function postToHost(type: string, payload: Record<string, unknown> = {}) {
  window.chrome?.webview?.postMessage({ type, ...payload })
}

export function subscribeToHost(
  listener: (message: HostEnvelope) => void,
): () => void {
  const handler = (event: MessageEvent<unknown>) => {
    if (
      typeof event.data === 'object' &&
      event.data !== null &&
      'type' in event.data
    ) {
      listener(event.data as HostEnvelope)
    }
  }

  window.chrome?.webview?.addEventListener('message', handler)
  return () => window.chrome?.webview?.removeEventListener('message', handler)
}

export function hasNativeHost() {
  return Boolean(window.chrome?.webview)
}
