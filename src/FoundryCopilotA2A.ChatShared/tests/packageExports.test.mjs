import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { browserPersistence, readConversations, writeConversations } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { isLoopback, readAuthMode, readFlag, readRequired } from '@foundry-copilot-a2a/chat-shared/config.ts'
import * as direct from '../src/chatSession.ts'

test('the shared modules resolve through the workspace package name', () => {
  assert.equal(typeof ChatSession, 'function')
  assert.equal(ChatSession, direct.ChatSession, 'the package export is the same module as the source file')
  for (const exported of [browserPersistence, readConversations, writeConversations, isLoopback, readAuthMode, readFlag, readRequired]) {
    assert.equal(typeof exported, 'function')
  }
})

test('a package-resolved session runs with a minimal transport', async () => {
  const chat = new ChatSession({
    prepare: () => ({ hasAttachment: false }),
    send: async ({ text }) => ({ turn: { answer: text.toUpperCase() } }),
  })
  await chat.send('ok', {})
  assert.equal(chat.getSnapshot().turns[0].answer, 'OK')
  assert.deepEqual(browserPersistence('key').key, 'key')
})
