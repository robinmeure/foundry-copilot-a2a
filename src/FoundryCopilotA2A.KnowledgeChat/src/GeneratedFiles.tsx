import { useState } from 'react'
import ChatIcon from '@foundry-copilot-a2a/chat-shared/ChatIcon.tsx'
import { formatFileSize, generatedFileLabel, GENERATED_FILE_TYPES, isGeneratedFileExpired,
  type GeneratedFile } from './generatedFiles.ts'

type Download = (file: GeneratedFile) => Promise<Blob>

/** Saves a blob through a temporary object URL; the URL is revoked once the browser has the file. */
function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.rel = 'noopener'
  document.body.append(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
}

export default function GeneratedFiles({ files, disabled, onDownload }: {
  files: readonly GeneratedFile[]; disabled: boolean; onDownload: Download
}) {
  if (!files.length) return null
  return (
    <section className="generated-files" aria-label="Generated files">
      {files.map(file => <GeneratedFileChip key={file.id} file={file} disabled={disabled} onDownload={onDownload} />)}
    </section>
  )
}

function GeneratedFileChip({ file, disabled, onDownload }: { file: GeneratedFile; disabled: boolean; onDownload: Download }) {
  const [state, setState] = useState<{ busy?: boolean; error?: string }>({})
  const expired = isGeneratedFileExpired(file)
  const status = state.error ?? (expired ? 'Expired. Ask again to recreate it.' : `Available until ${new Date(file.expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`)
  const download = async () => {
    if (isGeneratedFileExpired(file)) return setState({ error: 'Expired. Ask again to recreate it.' })
    setState({ busy: true })
    try {
      saveBlob(await onDownload(file), file.name)
      setState({})
    } catch (reason) {
      setState({ error: reason instanceof Error && reason.message ? reason.message : 'The file could not be downloaded.' })
    }
  }
  return (
    <div className="document-chip generated-file" data-state={state.error ? 'failed' : expired ? 'expired' : 'ready'}
      role="group" aria-label={`${file.name}: ${generatedFileLabel(file)}`} aria-busy={state.busy}>
      <span className="document-chip-type" aria-hidden="true">
        <ChatIcon name="file" /><span>{GENERATED_FILE_TYPES[file.mediaType].extension.toUpperCase()}</span>
      </span>
      <div className="document-chip-copy">
        <strong>{file.name}</strong>
        <div className="document-chip-detail">
          <span>{generatedFileLabel(file)} · {formatFileSize(file.size)}</span>
          <span role="status">{status}</span>
        </div>
      </div>
      <button type="button" className="secondary-button" disabled={disabled || expired || state.busy}
        onClick={() => void download()} aria-label={`Download ${file.name}`}>
        <ChatIcon name="arrow-down" /> {state.busy ? 'Downloading…' : 'Download'}
      </button>
    </div>
  )
}
