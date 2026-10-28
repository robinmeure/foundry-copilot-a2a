import assert from 'node:assert/strict'
import { test } from 'node:test'
import { mergeDictation, recognitionLanguage, savedSpeechLanguage, speechErrorMessage } from '../src/speechInput.ts'

test('a saved dictation language is restored only when it is still offered', () => {
  assert.equal(savedSpeechLanguage('nl-NL'), 'nl-NL')
  assert.equal(savedSpeechLanguage('xx-YY'), '')
  assert.equal(savedSpeechLanguage(null), '')
})

test('recognition uses the first full browser locale because Edge rejects a bare language', () => {
  assert.equal(recognitionLanguage(['nl-NL', 'en-US']), 'nl-NL')
  assert.equal(recognitionLanguage(['en', 'en-GB', 'nl']), 'en-GB')
  assert.equal(recognitionLanguage(['en', 'nl']), undefined)
  assert.equal(recognitionLanguage([]), undefined)
})

test('dictation fills an empty draft and appends to typed text with one space', () => {
  assert.equal(mergeDictation('', ' hello  world '), 'hello world')
  assert.equal(mergeDictation('Summarize', ' the report'), 'Summarize the report')
  assert.equal(mergeDictation('Summarize ', 'the report'), 'Summarize the report')
  assert.equal(mergeDictation('Summarize', '   '), 'Summarize')
})

test('silence and user cancellation are not reported as errors', () => {
  assert.equal(speechErrorMessage('no-speech'), undefined)
  assert.equal(speechErrorMessage('aborted'), undefined)
  assert.match(speechErrorMessage('not-allowed'), /Microphone access is blocked/)
  assert.match(speechErrorMessage('network'), /Microsoft Edge/)
  assert.match(speechErrorMessage('something-new'), /stopped unexpectedly/)
})
