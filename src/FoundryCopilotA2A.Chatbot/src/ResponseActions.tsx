import { useId, useState } from 'react'
import type { CitationBundle } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import ChatIcon from './ChatIcon.tsx'
import Sources from './Sources.ts'
import type { ResponseLink } from './responseLinks.ts'

export default function ResponseActions({ answer, citations, responseLinks = [] }: {
  answer: string; citations?: CitationBundle; responseLinks?: ResponseLink[]
}) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const [sourcesOpen, setSourcesOpen] = useState(false)
  const sourcesId = useId()
  const sourceCount = citations?.sources.length || responseLinks.length

  async function copyResponse() {
    try {
      await navigator.clipboard.writeText(answer)
      setCopyState('copied')
    } catch {
      setCopyState('failed')
    }
  }

  return (
    <div className="response-footer">
      <div className="response-actions">
        {answer && (
          <button type="button" className="copy-button" onClick={() => void copyResponse()} aria-label="Copy response">
            <ChatIcon name={copyState === 'copied' ? 'check' : 'copy'} />
            <span aria-live="polite">{copyState === 'copied' ? 'Copied' : 'Copy response'}</span>
          </button>
        )}
        {sourceCount > 0 && (
          <button type="button" className="sources-button" aria-expanded={sourcesOpen}
            aria-controls={sourcesId} onClick={() => setSourcesOpen((open) => !open)}>
            <ChatIcon name="link" />
            <span>Sources</span>
            <span className="source-count">{sourceCount}</span>
            <ChatIcon name={sourcesOpen ? 'chevron-up' : 'chevron-down'} />
          </button>
        )}
        {copyState === 'failed' && (
          <p className="copy-error" role="alert">Unable to copy. Select the response text and copy it manually.</p>
        )}
      </div>
      {sourceCount > 0 && (
        <div id={sourcesId} hidden={!sourcesOpen}>
          <Sources citations={citations} responseLinks={responseLinks} />
        </div>
      )}
    </div>
  )
}
