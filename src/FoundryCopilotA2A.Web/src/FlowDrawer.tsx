import { useLayoutEffect, useRef, type ReactNode } from 'react'
import './FlowDrawer.css'

interface FlowDrawerProps {
  children: ReactNode
  canFinish: boolean
  loading: boolean
  onFinish: () => void
  onDismiss: () => void
}

export function FlowDrawer({ children, canFinish, loading, onFinish, onDismiss }: FlowDrawerProps) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useLayoutEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    dialog.showModal()
    headingRef.current?.focus()
    return () => dialog.close()
  }, [])

  return (
    <dialog
      id="flow-configuration"
      className="flow-drawer"
      ref={dialogRef}
      aria-labelledby="flow-drawer-title"
      aria-describedby="flow-drawer-description"
      onCancel={(event) => {
        event.preventDefault()
        onDismiss()
      }}
    >
      <header className="flow-drawer-heading">
        <div>
          <h2 id="flow-drawer-title" ref={headingRef} tabIndex={-1}>Configure flow</h2>
          <p id="flow-drawer-description">Choose your route first. Done applies it and returns you to the conversation.</p>
        </div>
        <button type="button" className="flow-drawer-close" aria-label="Cancel flow configuration" onClick={onDismiss}>
          &times;
        </button>
      </header>
      <div className="flow-drawer-content">{children}</div>
      <footer className="flow-drawer-footer">
        <p id="flow-drawer-status" role="status">
          {loading ? 'Loading the selected route...'
            : canFinish ? 'Done applies this flow without sending a message.'
              : 'Complete a valid route before continuing.'}
        </p>
        <div>
          <button type="button" className="button secondary" onClick={onDismiss}>Cancel</button>
          <button type="button" className="button primary" disabled={!canFinish}
            aria-describedby="flow-drawer-status" onClick={onFinish}>Done</button>
        </div>
      </footer>
    </dialog>
  )
}
