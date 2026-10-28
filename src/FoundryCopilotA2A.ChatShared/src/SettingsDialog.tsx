import type { ReactNode } from 'react'
import ChatIcon from './ChatIcon.tsx'
import { useModalDialog } from './hooks.ts'
import { settingsPanelId } from './Sidebar.tsx'

export function SettingsSection({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <section className="settings-section" aria-labelledby={id}>
      <div className="settings-section-header">
        <h3 id={id}>{title}</h3>
        {children}
      </div>
    </section>
  )
}

/** Native side-panel modal: Escape, backdrop click and Done close it; focus returns to the opener. */
export default function SettingsDialog({ subtitle, contextId, conversationLabel, conversationNote, footerNote, children, onClose }: {
  subtitle: string
  contextId: string
  conversationLabel: string
  conversationNote: string
  footerNote?: string
  children: ReactNode
  onClose(): void
}) {
  const dialog = useModalDialog(true)
  return (
    <dialog ref={dialog} id={settingsPanelId} className="settings-backdrop" aria-labelledby="settings-panel-title"
      onCancel={(event) => { event.preventDefault(); onClose() }}
      onClick={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <aside className="settings-panel">
        <div className="settings-panel-header">
          <div className="settings-panel-title-group">
            <span className="settings-panel-icon" aria-hidden="true"><ChatIcon name="settings" /></span>
            <div>
              <h2 id="settings-panel-title">Settings</h2>
              <p>{subtitle}</p>
            </div>
          </div>
          <button type="button" autoFocus className="icon-button" aria-label="Close settings" title="Close (Esc)"
            onClick={onClose}><ChatIcon name="close" /></button>
        </div>
        <div className="settings-panel-body">
          {children}
          <details className="conversation-identity">
            <summary><ChatIcon name="shield" />Conversation details</summary>
            <div><span>{conversationLabel}</span><code>{contextId}</code><p>{conversationNote}</p></div>
          </details>
        </div>
        <div className="settings-panel-footer">
          {footerNote && <span className="settings-footer-note"><ChatIcon name="shield" /><span>{footerNote}</span></span>}
          <button type="button" className="primary-button settings-done-button" onClick={onClose}>Done</button>
        </div>
      </aside>
    </dialog>
  )
}
