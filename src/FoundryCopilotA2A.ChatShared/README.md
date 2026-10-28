# Chat shared core

`@foundry-copilot-a2a/chat-shared` is a private npm-workspace package used by the
[Orchestrator Chat](../FoundryCopilotA2A.Chatbot/README.md) and the
[Knowledge Chat](../FoundryCopilotA2A.KnowledgeChat/README.md). It has no build step; the apps
compile its TypeScript sources through Vite. Import modules by explicit subpath, for example
`@foundry-copilot-a2a/chat-shared/ChatShell.tsx`.

It owns everything both apps need and nothing specific to one backend:

| Area | Modules |
| --- | --- |
| Conversation engine | `chatSession.ts` (turn lifecycle against an injected `ChatTransport`), `conversationStore.ts` (versioned browser history with an app-supplied turn codec) |
| React state | `hooks.ts` (session, conversations, drafts) |
| Layout | `ChatShell.tsx`, `Sidebar.tsx`, `Transcript.tsx`, `Composer.tsx`, `SettingsDialog.tsx` and `style.css`, with slots for app-specific attachments, accessories, turn extras and settings sections |
| Replies | `ReplyBody.tsx`, `MarkdownAnswer.ts`, `Sources.ts`, `PlanTimeline.ts`, `ResponseActions.tsx`, `AdaptiveCards.ts` and related helpers |
| Authentication | `authentication.ts`, `SignedInApp.tsx`, `auth-redirect.ts` (MSAL redirect flow or loopback-only anonymous mode) |
| Startup | `bootstrap.tsx` and `config.ts` (shared Entra and auth-mode parsing) |
| Input | `speechInput.ts`, `useAutoSizeTextarea.ts` |

Each app supplies a transport (A2A through the adapter/APIM, or AG-UI to the Knowledge API),
its configuration and history key, and its own controls. The apps must not import each
other; shared behavior belongs here instead.

From the repository root:

```text
npm ci
npm test -w src/FoundryCopilotA2A.ChatShared
npm run lint -w src/FoundryCopilotA2A.ChatShared
npm run typecheck -w src/FoundryCopilotA2A.ChatShared
```