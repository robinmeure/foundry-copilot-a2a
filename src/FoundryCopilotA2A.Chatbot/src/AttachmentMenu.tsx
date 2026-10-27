import { type RefObject, useId, useRef, useState } from 'react'
import ChatIcon from './ChatIcon.tsx'

export default function AttachmentMenu({ buttonRef, disabled, sharePointAvailable, localDescription,
  sharePointUnavailableReason, onLocalFile, onSharePoint }: {
  buttonRef: RefObject<HTMLButtonElement | null>
  disabled: boolean
  sharePointAvailable: boolean
  localDescription: string
  sharePointUnavailableReason: string
  onLocalFile: () => void
  onSharePoint: () => void
}) {
  const id = useId()
  const menu = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)

  function choose(action: () => void) {
    menu.current?.hidePopover()
    action()
  }

  return <div className="attachment-menu">
    <button ref={buttonRef} type="button" className="icon-button attach-button"
      aria-label="Attach a file" title="Attach a file" aria-haspopup="menu" aria-expanded={open}
      popoverTarget={id} disabled={disabled}>
      <ChatIcon name="attach" />
    </button>
    <div ref={menu} id={id} className="attachment-options" popover="auto" role="menu"
      aria-label="Attachment options"
      onBeforeToggle={event => {
        if (event.newState !== 'open' || !buttonRef.current) return
        const bounds = buttonRef.current.getBoundingClientRect()
        const width = Math.min(280, window.innerWidth - 24)
        event.currentTarget.style.left = `${Math.max(12, Math.min(bounds.left, window.innerWidth - width - 12))}px`
        event.currentTarget.style.bottom = `${window.innerHeight - bounds.top + 8}px`
      }}
      onToggle={event => {
        const visible = event.newState === 'open'
        setOpen(visible)
        if (visible) event.currentTarget.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus()
      }}
      onKeyDown={event => {
        if (event.key === 'Tab') {
          menu.current?.hidePopover()
          buttonRef.current?.focus()
          return
        }
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
        event.preventDefault()
        const options = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')]
        const current = options.findIndex(option => option === document.activeElement)
        const index = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1
          : (current + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length
        options[index]?.focus()
      }}>
      <button type="button" role="menuitem" disabled={disabled} onClick={() => choose(onLocalFile)}>
        <ChatIcon name="file" />
        <span>Local file<small>{localDescription}</small></span>
      </button>
      <button type="button" role="menuitem" disabled={disabled || !sharePointAvailable}
        onClick={() => choose(onSharePoint)}>
        <ChatIcon name="sharepoint" />
        <span>SharePoint<small>{sharePointAvailable ? 'Browse sites and document libraries' : sharePointUnavailableReason}</small></span>
      </button>
    </div>
  </div>
}
