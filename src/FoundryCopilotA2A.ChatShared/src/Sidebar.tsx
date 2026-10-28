import type { ReactNode } from 'react'
import type { ChatAuthentication } from './authentication.ts'
import type { ChatTurn } from './chatSession.ts'
import ChatIcon from './ChatIcon.tsx'
import type { SavedConversation } from './conversationStore.ts'
import { isMobileLayout } from './hooks.ts'

export interface SidebarProps {
  brand: string
  auth: ChatAuthentication
  anonymous: boolean
  contextId: string
  conversations: readonly SavedConversation<ChatTurn>[]
  canStartNew: boolean
  canClearHistory: boolean
  historyOpen: boolean
  settingsOpen: boolean
  /** App-specific help paragraphs, for example how attachments are handled. */
  help: ReactNode
  onHistoryOpenChange(open: boolean): void
  onNewChat(): void
  onOpen(contextId: string): void
  onDelete(contextId: string): void
  onClearHistory(): void
  onOpenSettings(): void
  onSignOut(): void
}

export const settingsPanelId = 'backend-settings-panel'

export default function Sidebar(props: SidebarProps) {
  const { auth, anonymous, conversations, contextId, historyOpen } = props
  const accountName = auth.accountName || (anonymous ? 'Local user' : auth.signedIn ? 'Signed in' : 'Guest')
  const initials = accountName.trim().split(/\s+/).slice(0, 2).map((part) => part[0]).join('').toUpperCase()
  return (
    <aside className="sidebar" aria-label="Chat navigation">
      <div className="brand">
        <h1>{props.brand}<span className="brand-dot" aria-hidden="true">.</span></h1>
      </div>
      <nav className="sidebar-nav" aria-label="Conversation">
        <button className="nav-button" onClick={props.onNewChat} disabled={!props.canStartNew}>
          <ChatIcon name="new-chat" /><span>New chat</span>
        </button>
      </nav>
      <details className="chat-history" open={historyOpen}
        onToggle={event => props.onHistoryOpenChange(event.currentTarget.open)}
        onKeyDown={event => {
          if (event.key === 'Escape' && isMobileLayout()) {
            props.onHistoryOpenChange(false)
            event.currentTarget.querySelector('summary')?.focus()
          }
        }}>
        <summary>Conversations <span>{conversations.length}/30</span>
          <ChatIcon name={historyOpen ? 'chevron-up' : 'chevron-down'} /></summary>
        <div className="history-content">
          <p className="history-scope">Saved in this browser</p>
          {!conversations.length && <p className="history-empty">Your chats will appear here.</p>}
          <ul>
            {conversations.map((chat) => {
              const title = chat.turns[0]?.prompt || 'Untitled conversation'
              const current = chat.contextId === contextId
              return <li key={chat.contextId} data-current={current}>
                <button className="history-open" aria-current={current ? 'page' : undefined}
                  title={title} onClick={() => props.onOpen(chat.contextId)}>
                  <span>{title}</span>
                  <small>{new Date(chat.updatedAt).toLocaleDateString()} · {chat.turns.length} {chat.turns.length === 1 ? 'turn' : 'turns'}</small>
                </button>
                <button className="icon-button" aria-label={`Delete conversation: ${title}`} title="Delete conversation"
                  onClick={() => props.onDelete(chat.contextId)}><ChatIcon name="trash" /></button>
              </li>
            })}
          </ul>
        </div>
      </details>
      <div className="sidebar-footer">
        <button type="button" className="nav-button settings-nav-button" aria-expanded={props.settingsOpen}
          aria-haspopup="dialog" aria-controls={settingsPanelId} title="Settings" onClick={props.onOpenSettings}>
          <ChatIcon name="settings" />
          <span>Settings</span>
        </button>
        <details className="help-menu">
          <summary><ChatIcon name="help" /><span>Help &amp; guidance</span></summary>
          <div className="help-content">
            <p><kbd>Enter</kbd> sends your message. <kbd>Shift + Enter</kbd> adds a new line.</p>
            <p>Stop ends reception of a reply; the agent may still finish its work.</p>
            {props.help}
            <p>Browser storage is not encrypted. Delete sensitive chats before leaving a shared device.</p>
            <button type="button" className="text-button" disabled={!props.canClearHistory} onClick={props.onClearHistory}>
              Clear saved conversations
            </button>
          </div>
        </details>
        <div className="account">
          <span className="account-avatar" aria-hidden="true">{initials}</span>
          <div className="account-details">
            <span className="account-name" title={accountName}>{accountName}</span>
            <span className="account-caption">{anonymous ? 'Local mock session' : auth.signedIn ? 'Work account' : 'Not signed in'}</span>
          </div>
          {auth.signOut && auth.signedIn && (
            <button className="icon-button" aria-label="Sign out" title="Sign out" disabled={auth.busy}
              onClick={props.onSignOut}><ChatIcon name="sign-out" /></button>
          )}
        </div>
        {auth.signIn && !auth.signedIn && (
          <button className="primary-button" disabled={auth.busy} onClick={() => void auth.signIn?.()}>
            {auth.busy ? 'Signing in...' : 'Sign in'}
          </button>
        )}
      </div>
    </aside>
  )
}
