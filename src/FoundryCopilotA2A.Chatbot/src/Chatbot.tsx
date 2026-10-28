import { useMemo } from 'react'
import type { ChatAuthentication } from '@foundry-copilot-a2a/chat-shared/authentication.ts'
import ChatShell from '@foundry-copilot-a2a/chat-shared/ChatShell.tsx'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { browserPersistence } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { useChatSnapshot } from '@foundry-copilot-a2a/chat-shared/hooks.ts'
import { SettingsSection } from '@foundry-copilot-a2a/chat-shared/SettingsDialog.tsx'
import { a2aTurnCodec, createA2ATransport, type A2AInput } from './a2aTransport.ts'
import { historyKey, type ChatbotConfig } from './config.ts'
import { useInputFileAttachment } from './useInputFileAttachment.tsx'

export default function Chatbot({ config, auth }: { config: ChatbotConfig; auth: ChatAuthentication }) {
  const { getAccessToken, accountId } = auth
  const session = useMemo(() => new ChatSession<A2AInput>(
    createA2ATransport({ adapterBaseUrl: config.adapterBaseUrl, agentId: config.agentId,
      requireToken: config.authMode === 'entra', getAccessToken }),
    accountId ? { store: browserPersistence(historyKey(accountId, config)), codec: a2aTurnCodec } : undefined,
  ), [config, accountId, getAccessToken])
  const { isSending } = useChatSnapshot(session)
  const attachment = useInputFileAttachment(!auth.signedIn || auth.busy || isSending)
  const assistantName = `${config.agentName} · Copilot Studio`

  return <ChatShell session={session} auth={auth} anonymous={config.authMode === 'anonymous'}
    speechInput={config.speechInput} attachment={attachment}
    uploadNote={attachment.busy ? 'Reading attachment...' : undefined}
    onSend={(text) => session.send(text, { files: attachment.file ? [attachment.file] : [] })}
    copy={{
      brand: config.agentName,
      assistantName,
      welcomeHeading: 'Talk to your Copilot Studio agent.',
      welcomeBody: `${config.agentName} handles the conversation and its configured specialists through the A2A adapter.`,
      placeholder: `Message ${config.agentName}...`,
      messageLabel: `Message to ${assistantName}`,
      fileCaption: 'sent with this message',
      clearHistoryConfirm: 'Delete all saved chats for this account, endpoint and agent in this browser?',
      help: <>
        <p>Up to 30 chats are saved in this browser for your account and agent. This is not a list of all Copilot Studio conversations.</p>
        <p>Files are sent only with their selected message. Only filenames, types and sizes are saved locally, not file bytes. The provider may retain uploaded files.</p>
        <p>Provider context can expire or be lost after an adapter restart; reopening cannot restore uploaded file content.</p>
      </>,
    }}
    settings={{
      subtitle: 'Agent, files and conversation',
      conversationLabel: 'A2A conversation ID',
      conversationNote: 'Saved in this browser. Provider context may expire; reopening does not reupload files.',
      sections: <>
        <SettingsSection id="agent-settings-title" title="Agent">
          <p>{config.agentName} is a Copilot Studio agent reached through the A2A adapter{config.authMode === 'entra' ? ' with your delegated identity' : ''}. It manages its own tools and specialists.</p>
          {config.authMode === 'anonymous' && <p>Local development session. The adapter must run in Mock mode for Azure-free use.</p>}
        </SettingsSection>
        <SettingsSection id="file-settings-title" title="Files">
          <p>Experimental file input · 1 file, up to 5 MB · TXT, CSV, PDF, DOCX, PNG, JPEG. Agent file reading is not verified. Gateways may have lower limits.</p>
        </SettingsSection>
      </>,
    }} />
}
