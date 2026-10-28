import { bootstrapChatApp } from '@foundry-copilot-a2a/chat-shared/bootstrap.tsx'
import '@foundry-copilot-a2a/chat-shared/style.css'
import './knowledge.css'
import { knowledgeScope, readKnowledgeChatConfig } from './config.ts'
import KnowledgeChat from './KnowledgeChat.tsx'

await bootstrapChatApp({
  readConfig: () => readKnowledgeChatConfig(import.meta.env, import.meta.env.DEV, window.location.origin),
  title: () => 'Knowledge Chat',
  scope: knowledgeScope,
  key: (config) => config.knowledgeBaseUrl,
  render: (config, auth) => <KnowledgeChat config={config} auth={auth} />,
  configurationHelp: <p>Configure this app's <code>.env.local</code> with its SPA registration, the Knowledge API
    registration and the Knowledge API URL, then restart the frontend.</p>,
})
