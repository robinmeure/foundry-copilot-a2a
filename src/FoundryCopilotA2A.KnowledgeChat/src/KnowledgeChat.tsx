import { useMemo, useState, type SetStateAction } from 'react'
import type { ChatAuthentication } from '@foundry-copilot-a2a/chat-shared/authentication.ts'
import ChatShell from '@foundry-copilot-a2a/chat-shared/ChatShell.tsx'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { browserPersistence } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { useChatSnapshot } from '@foundry-copilot-a2a/chat-shared/hooks.ts'
import { SettingsSection } from '@foundry-copilot-a2a/chat-shared/SettingsDialog.tsx'
import ActionCards from './ActionCards.tsx'
import { historyKey, type KnowledgeChatConfig } from './config.ts'
import GeneratedFiles from './GeneratedFiles.tsx'
import { downloadGeneratedFile } from './knowledgeClient.ts'
import KnowledgeSources from './KnowledgeSources.tsx'
import { defaultKnowledgeSelection, knowledgeRequestSelection, selectedKnowledgeSources,
  withOfferedKnowledgeSources, type KnowledgeSelectionState } from './knowledgeSelection.ts'
import { createKnowledgeTransport, knowledgeTurnCodec, resolveGraphAction, type KnowledgeInput, type KnowledgeTurn } from './knowledgeTransport.ts'
import SourceSettings from './SourceSettings.tsx'
import { useKnowledgeCapabilities } from './useKnowledgeCapabilities.ts'
import { useKnowledgeDocumentAttachment } from './useKnowledgeDocumentAttachment.tsx'

const assistantName = 'Local orchestrator'

export default function KnowledgeChat({ config, auth }: { config: KnowledgeChatConfig; auth: ChatAuthentication }) {
  const { getAccessToken: token, accountId } = auth
  const endpoint = useMemo(() => ({ baseUrl: config.knowledgeBaseUrl, token }), [config.knowledgeBaseUrl, token])
  const session = useMemo(() => new ChatSession<KnowledgeInput, KnowledgeTurn>(createKnowledgeTransport(endpoint),
    accountId ? { store: browserPersistence(historyKey(accountId, config)), codec: knowledgeTurnCodec } : undefined,
  ), [endpoint, accountId, config])
  const { isSending, contextId } = useChatSnapshot(session)
  const ready = auth.signedIn && !auth.busy
  const capabilityState = useKnowledgeCapabilities(config.knowledgeBaseUrl, token, ready)
  const [storedSelection, setSelection] = useState(defaultKnowledgeSelection)
  const { capabilities } = capabilityState
  // Sources the API offers for the first time start selected; changes apply on top of that view.
  const selection = withOfferedKnowledgeSources(storedSelection, capabilities)
  const changeSelection = (action: SetStateAction<KnowledgeSelectionState>) => setSelection(previous => {
    const current = withOfferedKnowledgeSources(previous, capabilities)
    return typeof action === 'function' ? action(current) : action
  })
  const attachment = useKnowledgeDocumentAttachment(!ready || isSending, contextId)

  function send(text: string) {
    const { capabilities, agents } = capabilityState
    const apimAgents = capabilities?.a2a ? agents : undefined
    const sources = selectedKnowledgeSources(selection.sources, capabilities)
    const document = attachment.document
    return attachment.track(document, contextId, (onDocumentIndexed) => session.send(text, {
      document, onDocumentIndexed, selection: knowledgeRequestSelection(sources, apimAgents, selection.excludedAgents),
    }))
  }

  return <ChatShell session={session} auth={auth} anonymous={config.authMode === 'anonymous'}
    speechInput={config.speechInput} attachment={attachment} onSend={send}
    onConversationChange={attachment.closePicker}
    composerAccessory={<KnowledgeSources key={contextId} baseUrl={config.knowledgeBaseUrl} conversation={contextId}
      token={token} disabled={!ready || isSending} revision={attachment.revision}
      pickerOpen={attachment.pickerOpen} onPickerClose={attachment.closePicker} opener={attachment.opener} />}
    renderTurnExtras={(turn) => turn.actions?.length || turn.generatedFiles?.length ? <>
      {turn.generatedFiles?.length ? <GeneratedFiles files={turn.generatedFiles} disabled={!ready}
        onDownload={(file) => downloadGeneratedFile(endpoint.baseUrl, contextId, file, endpoint.token)} /> : null}
      {turn.actions?.length ? <ActionCards actions={turn.actions} disabled={!ready}
        onDecide={(actionId, decision) => void resolveGraphAction(session, endpoint,
          { contextId, turnId: turn.id, actionId }, decision)} /> : null}
    </> : null}
    copy={{
      brand: 'Knowledge Chat',
      assistantName,
      welcomeHeading: 'Ask across your work.',
      welcomeBody: 'Import a document or ask a question. The local orchestrator can use your selected Microsoft 365 sources and available APIM agents.',
      placeholder: 'Ask the local orchestrator...',
      messageLabel: `Message to ${assistantName}`,
      fileCaption: 'submitted for backend indexing',
      clearHistoryConfirm: 'Delete all saved chats for this account and Knowledge API in this browser?',
      help: <>
        <p>Up to 30 chats are saved in this browser for your account and Knowledge API.</p>
        <p>Backend documents are indexed for this conversation until removed or expired. Deleting browser history does not delete backend documents; remove them in Documents &amp; SharePoint first.</p>
      </>,
    }}
    settings={{
      subtitle: 'Tools, files and conversation',
      conversationLabel: 'Backend conversation ID',
      conversationNote: 'Saved in this browser. Document retrieval is rebuilt on every question; expired sources must be imported again.',
      sections: <SourceSettings selection={selection} onChange={changeSelection} capabilityState={capabilityState} disabled={isSending}>
        <SettingsSection id="orchestration-settings-title" title="Orchestration">
          <p>The .NET Knowledge API orchestrates documents and selected tools with Microsoft Agent Framework. Live mode still uses an Azure-hosted model, not an offline LLM.</p>
          {config.authMode === 'anonymous' && <p>Local development session. The Knowledge API must run in Mock mode for Azure-free use.</p>}
        </SettingsSection>
        <SettingsSection id="file-settings-title" title="Files">
          <p>PDF, DOCX, TXT, Markdown, CSV. Up to 20 MiB / 300 pages. Indexed documents stay with this conversation.</p>
        </SettingsSection>
      </SourceSettings>,
    }} />
}
