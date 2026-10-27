// Builders for AG-UI event streams as the Knowledge API sends them from POST /api/chat/stream.

export const runStarted = (threadId = 'c') => ({ type: 'RUN_STARTED', threadId, runId: 'run' })
/** A user-facing status travels in step metadata; steps without metadata are protocol bookkeeping. */
export const stepStarted = (stepName, metadata) => ({ type: 'STEP_STARTED', stepName, ...(metadata ? { metadata } : {}) })
export const stepFinished = (stepName, metadata) => ({ type: 'STEP_FINISHED', stepName, ...(metadata ? { metadata } : {}) })
export const textStart = { type: 'TEXT_MESSAGE_START', messageId: 'answer', role: 'assistant' }
export const textContent = (delta) => ({ type: 'TEXT_MESSAGE_CONTENT', messageId: 'answer', delta })
export const textEnd = { type: 'TEXT_MESSAGE_END', messageId: 'answer' }
export const runFinished = (result, threadId = 'c') => ({ type: 'RUN_FINISHED', threadId, runId: 'run', result })
/** RUN_ERROR carries the API's ChatStreamError (status, code, message, challenge, retryAfter) as metadata. */
export const runError = (metadata) => ({ type: 'RUN_ERROR', message: metadata.message, code: metadata.code, metadata })

export const agUiFrames = (events) => events.map(event => `data: ${JSON.stringify(event)}\n\n`).join('')
export const agUiResponse = (events) =>
  new Response(agUiFrames(events), { headers: { 'Content-Type': 'text/event-stream' } })

/** A complete successful run: optional answer text streamed in chunks, then the validated result. */
export const agUiAnswer = (result, deltas = []) => agUiResponse([
  runStarted(),
  ...(deltas.length ? [textStart, ...deltas.map(textContent), textEnd] : []),
  runFinished(result),
])
