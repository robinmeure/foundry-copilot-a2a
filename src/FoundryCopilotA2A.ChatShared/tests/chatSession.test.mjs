import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { deferred, fakeTransport, memoryStorage, noteCodec } from './fakeTransport.mjs'

const persisted = (storage = memoryStorage(), key = 'chat') => ({ store: { key, storage }, codec: noteCodec })
const settle = () => new Promise((resolve) => setImmediate(resolve))

test('a turn moves from sending to succeeded and the transport receives the conversation context', async () => {
  const transport = fakeTransport(async ({ text, update }) => {
    update({ progress: 'Thinking', phase: 'working' })
    update((turn) => ({ answer: `${turn.prompt} partial`, phase: 'receiving' }))
    return { turn: { answer: `Echo: ${text}` } }
  })
  const chat = new ChatSession(transport)
  const snapshots = []
  chat.subscribe(() => snapshots.push(chat.getSnapshot()))
  const contextId = chat.getSnapshot().contextId
  await chat.send('  Hello  ', { responder: { id: 'agent', name: 'Agent' } })

  const [first] = snapshots
  assert.equal(first.isSending, true)
  assert.deepEqual([first.turns[0].status, first.turns[0].phase, first.turns[0].prompt, first.turns[0].answer],
    ['sending', 'sending', 'Hello', ''])
  assert.deepEqual(first.turns[0].responder, { id: 'agent', name: 'Agent' })
  assert.ok(snapshots.some((s) => s.turns[0].progress === 'Thinking' && s.turns[0].phase === 'working'))
  assert.ok(snapshots.some((s) => s.turns[0].answer === 'Hello partial' && s.turns[0].status === 'sending'))
  const final = chat.getSnapshot()
  assert.equal(final.isSending, false)
  assert.deepEqual([final.turns[0].status, final.turns[0].phase, final.turns[0].progress, final.turns[0].answer],
    ['succeeded', 'finalizing', undefined, 'Echo: Hello'])
  assert.equal(final.turns[0].error, undefined)

  await chat.send('Again', {})
  const [, second] = transport.requests
  assert.equal(second.contextId, contextId)
  assert.equal(second.text, 'Again')
  assert.deepEqual(second.input, {})
  assert.deepEqual(second.turns.map((turn) => turn.prompt), ['Hello'])
  assert.ok(second.signal instanceof AbortSignal)
})

test('conversation handles are merged newest first, and a failure keeps the error but clears them', async () => {
  const replies = [
    async () => ({ turn: { answer: 'One' }, conversationHandles: ['h1'] }),
    async () => ({ turn: { answer: 'Two' }, conversationHandles: ['h2'] }),
    async ({ update }) => {
      update({ answer: 'Partial', citations: { schemaVersion: '1', sources: [], citations: [] } })
      throw new Error('Backend down')
    },
    async () => { throw 'not an error' },
  ]
  const transport = fakeTransport((request) => replies.shift()(request))
  const chat = new ChatSession(transport)
  await chat.send('First', {})
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['h1'])
  await chat.send('Second', {})
  assert.deepEqual(transport.requests[1].conversationHandles, ['h1'])
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['h2', 'h1'])

  await chat.send('Third', {})
  assert.deepEqual(transport.requests[2].conversationHandles, ['h2', 'h1'])
  const failed = chat.getSnapshot().turns[2]
  assert.deepEqual([failed.status, failed.error, failed.answer, failed.citations], ['failed', 'Backend down', 'Partial', undefined])
  assert.deepEqual(chat.getSnapshot().conversationHandles, [])
  assert.equal(chat.getSnapshot().isSending, false)

  await chat.send('Fourth', {})
  assert.deepEqual(transport.requests[3].conversationHandles, [])
  assert.equal(chat.getSnapshot().turns[3].error, 'Unable to send the message.')
})

test('stop marks the turn failed, aborts the request and ignores late updates', async () => {
  const reply = deferred()
  let request
  const chat = new ChatSession(fakeTransport((value) => {
    request = value
    value.update({ answer: 'Partial', phase: 'receiving', progress: 'Working' })
    return reply.promise
  }))
  const pending = chat.send('Long request', {})
  await settle()
  chat.stop()
  assert.equal(request.signal.aborted, true)
  assert.equal(chat.getSnapshot().isSending, false)
  request.update({ answer: 'Late update' })
  reply.resolve({ turn: { answer: 'Late answer' }, conversationHandles: ['late'] })
  await pending
  const turn = chat.getSnapshot().turns[0]
  assert.deepEqual([turn.status, turn.answer, turn.progress], ['failed', 'Partial', undefined])
  assert.match(turn.error, /Response stopped/)
  assert.deepEqual(chat.getSnapshot().conversationHandles, [])
  chat.stop()
  assert.equal(chat.getSnapshot().turns[0].status, 'failed', 'stopping when idle is a no-op')
})

test('transports that only validate the final event discard partial text on failure and on stop', async () => {
  const failing = new ChatSession(fakeTransport(async ({ update }) => {
    update({ answer: 'Unvalidated' })
    throw new Error('Stream failed')
  }, { discardsPartialAnswer: true }))
  await failing.send('Question', {})
  assert.deepEqual([failing.getSnapshot().turns[0].status, failing.getSnapshot().turns[0].answer,
    failing.getSnapshot().turns[0].error], ['failed', '', 'Stream failed'])

  const reply = deferred()
  const stopped = new ChatSession(fakeTransport(({ update }) => {
    update({ answer: 'Unvalidated' })
    return reply.promise
  }, { discardsPartialAnswer: true }))
  const pending = stopped.send('Question', {})
  await settle()
  assert.equal(stopped.getSnapshot().turns[0].answer, 'Unvalidated')
  stopped.stop()
  reply.resolve({ turn: { answer: 'Late' } })
  await pending
  assert.deepEqual([stopped.getSnapshot().turns[0].status, stopped.getSnapshot().turns[0].answer], ['failed', ''])
  assert.match(stopped.getSnapshot().turns[0].error, /Response stopped/)
})

test('an attachment-only send asks for a summary and keeps the file metadata on the turn', async () => {
  const transport = fakeTransport()
  const chat = new ChatSession(transport)
  await chat.send('   ', { file: 'notes.txt' })
  const [turn] = chat.getSnapshot().turns
  assert.equal(turn.prompt, 'Please summarize the attached file.')
  assert.equal(transport.requests[0].text, 'Please summarize the attached file.')
  assert.deepEqual(turn.files, [{ name: 'notes.txt', mediaType: 'text/plain', size: 5 }])
  await chat.send('No file', {})
  assert.equal(chat.getSnapshot().turns[1].files, undefined)
})

test('empty prompts and invalid input reject before a turn is shown', async () => {
  const transport = fakeTransport()
  const chat = new ChatSession(transport)
  await assert.rejects(chat.send('', {}), /Enter a message before sending/)
  await assert.rejects(chat.send(' \n ', {}), /Enter a message before sending/)
  await assert.rejects(chat.send('Question', { invalid: true }), /Invalid input/)
  assert.deepEqual(chat.getSnapshot().turns, [])
  assert.equal(chat.getSnapshot().isSending, false)
  assert.equal(transport.requests.length, 0)
  await chat.send('Valid', {})
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
})

test('a second send while a reply is in flight is rejected', async () => {
  const reply = deferred()
  const transport = fakeTransport(() => reply.promise)
  const chat = new ChatSession(transport)
  const pending = chat.send('First', {})
  await assert.rejects(chat.send('Second', {}), /current reply/)
  assert.equal(chat.getSnapshot().turns.length, 1)
  reply.resolve({ turn: { answer: 'Done' } })
  await pending
  assert.equal(transport.requests.length, 1)
})

test('reset rotates the context, keeps the old chat in history and stops late replies leaking in', async () => {
  const replies = [async () => ({ turn: { answer: 'Old' }, conversationHandles: ['old'] })]
  const late = deferred()
  replies.push(() => late.promise)
  const chat = new ChatSession(fakeTransport((request) => replies.shift()(request)))
  await chat.send('Saved', {})
  const first = chat.getSnapshot().contextId
  const pending = chat.send('Interrupted', {})
  chat.reset()
  late.resolve({ turn: { answer: 'Late' }, conversationHandles: ['late'] })
  await pending
  const snapshot = chat.getSnapshot()
  assert.notEqual(snapshot.contextId, first)
  assert.deepEqual([snapshot.turns, snapshot.conversationHandles, snapshot.isSending], [[], [], false])
  const saved = snapshot.conversations.find((conversation) => conversation.contextId === first)
  assert.deepEqual(saved.turns.map((turn) => [turn.prompt, turn.status]), [['Saved', 'succeeded'], ['Interrupted', 'failed']])
  assert.match(saved.turns[1].error, /Response stopped/)
})

test('app-specific turn fields round-trip through the codec, while display-only data stays in memory', async () => {
  const storage = memoryStorage()
  const chat = new ChatSession(fakeTransport(async () => ({ turn: {
    answer: 'Answer', note: 'kept', cards: { schemaVersion: '1', cards: [] }, planUpdates: [{ eventId: 'plan' }],
    citations: { schemaVersion: '1', sources: [], citations: [] }, activityUpdates: ['Searched'],
  } })), persisted(storage))
  await chat.send('Question', { file: 'notes.txt', responder: { name: 'Agent' } })
  assert.equal(chat.getSnapshot().turns[0].note, 'kept')
  assert.doesNotMatch(storage.getItem('chat'), /planUpdates|cards/)

  const restored = new ChatSession(fakeTransport(), persisted(storage)).getSnapshot()
  assert.equal(restored.storageError, undefined)
  assert.equal(restored.contextId, chat.getSnapshot().contextId)
  const [turn] = restored.turns
  assert.deepEqual({ prompt: turn.prompt, answer: turn.answer, status: turn.status, phase: turn.phase, note: turn.note,
    files: turn.files, responder: turn.responder, activityUpdates: turn.activityUpdates, citations: turn.citations }, {
    prompt: 'Question', answer: 'Answer', status: 'succeeded', phase: 'finalizing', note: 'kept',
    files: [{ name: 'notes.txt', mediaType: 'text/plain', size: 5 }], responder: { name: 'Agent' },
    activityUpdates: ['Searched'], citations: { schemaVersion: '1', sources: [], citations: [] },
  })
  assert.equal(turn.cards, undefined)
  assert.equal(turn.planUpdates, undefined)
})

test('retention is capped at the 30 most recent conversations', async () => {
  const storage = memoryStorage()
  const chat = new ChatSession(fakeTransport(), persisted(storage))
  for (let index = 0; index < 32; index++) {
    chat.reset()
    await chat.send(`Question ${index}`, {})
  }
  const { conversations } = chat.getSnapshot()
  assert.equal(conversations.length, 30)
  assert.equal(conversations[0].turns[0].prompt, 'Question 31')
  assert.equal(conversations.at(-1).turns[0].prompt, 'Question 2')
  assert.equal(JSON.parse(storage.getItem('chat')).conversations.length, 30)
  assert.equal(new ChatSession(fakeTransport(), persisted(storage)).getSnapshot().storageError, undefined)
})

test('a turn saved while sending is restored as interrupted rather than streaming forever', async () => {
  const storage = memoryStorage()
  const reply = deferred()
  const chat = new ChatSession(fakeTransport(({ update }) => {
    update({ answer: 'Half' })
    return reply.promise
  }), persisted(storage))
  const pending = chat.send('Pending', {})
  const [turn] = new ChatSession(fakeTransport(), persisted(storage)).getSnapshot().turns
  assert.equal(turn.status, 'failed')
  assert.equal(turn.phase, 'finalizing')
  assert.match(turn.error, /interrupted/)
  chat.stop()
  reply.resolve({ turn: { answer: 'Late' } })
  await pending
})

test('findTurn and updateTurnExtras reach a conversation that is no longer active', async () => {
  const storage = memoryStorage()
  const chat = new ChatSession(fakeTransport(async () => ({ turn: { answer: 'Answer', note: 'original' } })), persisted(storage))
  await chat.send('In the first chat', {})
  const { contextId: first, turns: [turn] } = chat.getSnapshot()
  chat.reset()
  await chat.send('In the second chat', {})
  const active = chat.getSnapshot()

  assert.equal(chat.findTurn(first, turn.id).prompt, 'In the first chat')
  assert.equal(chat.findTurn(first, 'missing'), undefined)
  assert.equal(chat.findTurn('missing', turn.id), undefined)
  chat.updateTurnExtras(first, turn.id, (current) => ({ note: `${current.note} → updated` }))
  assert.equal(chat.findTurn(first, turn.id).note, 'original → updated')
  assert.deepEqual(chat.getSnapshot().turns, active.turns, 'the active chat is untouched')
  assert.equal(chat.getSnapshot().contextId, active.contextId)
  chat.updateTurnExtras('missing', turn.id, () => ({ note: 'ignored' }))

  const reloaded = new ChatSession(fakeTransport(), persisted(storage))
  assert.equal(reloaded.findTurn(first, turn.id).note, 'original → updated')
  reloaded.open(first)
  assert.equal(reloaded.getSnapshot().turns[0].note, 'original → updated')
})

test('updateTurnExtras never patches a turn that is still sending', async () => {
  const reply = deferred()
  const chat = new ChatSession(fakeTransport(() => reply.promise))
  const pending = chat.send('Question', {})
  const { contextId, turns: [turn] } = chat.getSnapshot()
  chat.updateTurnExtras(contextId, turn.id, () => ({ note: 'too early' }))
  assert.equal(chat.findTurn(contextId, turn.id).note, undefined)
  reply.resolve({ turn: { answer: 'Done' } })
  await pending
  chat.updateTurnExtras(contextId, turn.id, () => ({ note: 'after' }))
  assert.equal(chat.findTurn(contextId, turn.id).note, 'after')
})
