import { type RefObject, useRef, useState } from 'react'
import { fileSizeLabel } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import type { ComposerAttachment } from '@foundry-copilot-a2a/chat-shared/Composer.tsx'
import AttachmentMenu from './AttachmentMenu.tsx'
import DocumentChip from './DocumentChip.tsx'
import { validateKnowledgeFile } from './knowledgeClient.ts'

export interface KnowledgeDocumentAttachment extends ComposerAttachment {
  /** The local document to index with the next message. */
  document?: File
  /** Bumps whenever indexed documents may have changed, so the document list reloads. */
  revision: number
  pickerOpen: boolean
  closePicker(): void
  opener: RefObject<HTMLButtonElement | null>
  /**
   * Shows an indexing chip while `send` runs, and refreshes the document list once the document
   * is indexed or the send ends.
   */
  track(document: File | undefined, contextId: string, send: (onDocumentIndexed: () => void) => Promise<void>): Promise<void>
}

/** A local document or SharePoint import that the Knowledge API indexes for the conversation. */
export function useKnowledgeDocumentAttachment(disabled: boolean, contextId: string): KnowledgeDocumentAttachment {
  const [document, setDocument] = useState<File>()
  const [error, setError] = useState<string>()
  const [upload, setUpload] = useState<{ file: File; contextId: string }>()
  const [revision, setRevision] = useState(0)
  const [pickerOpen, setPickerOpen] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const opener = useRef<HTMLButtonElement>(null)
  const refresh = () => setRevision((previous) => previous + 1)

  function clear() {
    setDocument(undefined)
    setError(undefined)
    if (input.current) input.current.value = ''
  }

  function choose(selected?: File) {
    clear()
    if (!selected) return
    try {
      validateKnowledgeFile(selected)
      setDocument(selected)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to select this document.')
    }
  }

  async function track(file: File | undefined, target: string, send: (onDocumentIndexed: () => void) => Promise<void>) {
    const current = file ? { file, contextId: target } : undefined
    const finish = () => setUpload((previous) => previous === current ? undefined : previous)
    let indexed = false
    setUpload(current)
    try {
      await send(() => {
        indexed = true
        refresh()
        finish()
      })
    } finally {
      if (file && !indexed) refresh()
      finish()
    }
  }

  const indexing = upload?.contextId === contextId ? upload : undefined
  return {
    document,
    ready: Boolean(document),
    busy: false,
    error,
    clear,
    revision,
    pickerOpen,
    closePicker: () => setPickerOpen(false),
    opener,
    track,
    preview: (document || indexing) && <div className="document-attachments" role="status">
      {document && <DocumentChip name={document.name} state="pending"
        detail={`${fileSizeLabel(document.size)} · Send to index for this chat`} onRemove={clear} />}
      {indexing && <DocumentChip name={indexing.file.name} state="indexing"
        detail={`${fileSizeLabel(indexing.file.size)} · Preparing for retrieval`} />}
    </div>,
    control: <>
      <input ref={input} type="file" className="sr-only" tabIndex={-1} aria-label="Choose a local file"
        accept=".pdf,.docx,.txt,.md,.csv" disabled={disabled} onChange={(event) => choose(event.target.files?.[0])} />
      <AttachmentMenu buttonRef={opener} disabled={disabled} sharePointAvailable
        localDescription="Index a document · Up to 20 MiB" sharePointUnavailableReason="Knowledge API not configured"
        onLocalFile={() => input.current?.click()} onSharePoint={() => setPickerOpen(true)} />
    </>,
  }
}
