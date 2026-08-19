import { Component, type ErrorInfo, type ReactNode } from 'react'
import { CircleAlert, FolderOpen, RotateCcw } from 'lucide-react'
import { reportFrontendError } from '../lib/frontendDiagnostics'

type EditorErrorBoundaryProps = {
  children: ReactNode
  resetKey: string
  onOpenProjects: () => void
}

type EditorErrorBoundaryState = {
  error: Error | null
}

export class EditorErrorBoundary extends Component<
  EditorErrorBoundaryProps,
  EditorErrorBoundaryState
> {
  state: EditorErrorBoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): EditorErrorBoundaryState {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    reportFrontendError('editor-boundary', error, info.componentStack ?? '')
  }

  componentDidUpdate(previousProps: EditorErrorBoundaryProps) {
    if (previousProps.resetKey !== this.props.resetKey && this.state.error) {
      this.setState({ error: null })
    }
  }

  private retry = () => this.setState({ error: null })

  render() {
    if (!this.state.error) return this.props.children
    return (
      <main className="editor-error" id="editor-workspace" role="alert">
        <span className="editor-error__icon"><CircleAlert size={30} /></span>
        <strong>Không thể hiển thị không gian chỉnh sửa</strong>
        <p>
          Giao diện dự án vừa gặp lỗi. Dữ liệu dự án vẫn được giữ nguyên và lỗi đã được ghi lại.
        </p>
        <div className="editor-error__actions">
          <button type="button" onClick={this.retry}>
            <RotateCcw size={15} /> Thử lại
          </button>
          <button type="button" onClick={this.props.onOpenProjects}>
            <FolderOpen size={15} /> Chọn dự án khác
          </button>
        </div>
      </main>
    )
  }
}
