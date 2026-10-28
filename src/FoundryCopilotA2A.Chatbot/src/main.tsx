import { bootstrapChatApp } from '@foundry-copilot-a2a/chat-shared/bootstrap.tsx'
import '@foundry-copilot-a2a/chat-shared/style.css'
import { createDelegatedLoginRequest } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import Chatbot from './Chatbot.tsx'
import { readChatbotConfig } from './config.ts'

await bootstrapChatApp({
  readConfig: () => readChatbotConfig(import.meta.env, import.meta.env.DEV, window.location.origin),
  title: (config) => `${config.agentName} Chat`,
  scope: (config) => createDelegatedLoginRequest(config).scopes[0],
  key: (config) => `${config.adapterBaseUrl}:${config.agentId}`,
  render: (config, auth) => <Chatbot config={config} auth={auth} />,
  configurationHelp: <p>Configure this app's <code>.env.local</code> with its dedicated SPA registration, the adapter
    API registration, and the Orchestrator agent ID, then restart the frontend.</p>,
})
