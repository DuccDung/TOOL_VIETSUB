import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { reportFrontendError } from './lib/frontendDiagnostics'
import './styles.css'

window.addEventListener('error', (event) => {
  reportFrontendError('window-error', event.error ?? event.message)
})

window.addEventListener('unhandledrejection', (event) => {
  reportFrontendError('unhandled-rejection', event.reason)
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
