import { useRef, useState } from 'react'
import { fileAccept, fileSizeLabel, readInputFile, type InputFile } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import ChatIcon from '@foundry-copilot-a2a/chat-shared/ChatIcon.tsx'
import type { ComposerAttachment } from '@foundry-copilot-a2a/chat-shared/Composer.tsx'

/** One experimental file, read into memory and sent inline with the next message. */
export function useInputFileAttachment(disabled: boolean): ComposerAttachment & { file?: InputFile } {
  const [file, setFile] = useState<InputFile>()
  const [reading, setReading] = useState(false)
  const [error, setError] = useState<string>()
  const input = useRef<HTMLInputElement>(null)
  // Ignores reads that finish after the selection was cleared or replaced.
  const revision = useRef(0)

  function clear() {
    revision.current++
    setFile(undefined)
    setReading(false)
    setError(undefined)
    if (input.current) input.current.value = ''
  }

  async function choose(selected?: File) {
    clear()
    if (!selected) return
    const current = revision.current
    setReading(true)
    try {
      const loaded = await readInputFile(selected)
      if (revision.current === current) setFile(loaded)
    } catch (reason) {
      if (revision.current === current) setError(reason instanceof Error ? reason.message : 'Unable to read this file.')
    } finally {
      if (revision.current === current) setReading(false)
    }
  }

  return {
    file,
    ready: Boolean(file),
    busy: reading,
    error,
    clear,
    preview: file && <div className="pending-file" role="status">
      <ChatIcon name="attach" /><span>{file.name}<small>{fileSizeLabel(file.size)}</small></span>
      <button type="button" className="icon-button" aria-label={`Remove ${file.name}`} onClick={clear}><ChatIcon name="close" /></button>
    </div>,
    control: <>
      <input ref={input} type="file" className="sr-only" tabIndex={-1} aria-label="Choose a local file"
        accept={fileAccept} disabled={disabled || reading} onChange={(event) => void choose(event.target.files?.[0])} />
      <button type="button" className="icon-button" aria-label="Attach a file" title="Attach a file · Experimental, up to 5 MB"
        disabled={disabled || reading} onClick={() => input.current?.click()}><ChatIcon name="attach" /></button>
    </>,
  }
}
