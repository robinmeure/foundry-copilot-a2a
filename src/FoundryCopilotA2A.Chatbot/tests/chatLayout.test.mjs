import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

// Structural guards complement the rendered geometry/keyboard fixture documented in README.
const source = readFileSync(new URL('../src/Chatbot.tsx', import.meta.url), 'utf8')
const tree = ts.createSourceFile('Chatbot.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const elements = []
function visit(node, parents = []) {
  const opening = ts.isJsxElement(node) ? node.openingElement : ts.isJsxSelfClosingElement(node) ? node : undefined
  if (opening) {
    elements.push({ node, opening, parents })
    parents = [...parents, opening]
  }
  ts.forEachChild(node, child => visit(child, parents))
}
visit(tree)
const attribute = (element, name) => element.attributes.properties.find(property =>
  ts.isJsxAttribute(property) && property.name.text === name)?.initializer
const value = (element, name) => attribute(element, name)?.getText(tree)
const byClass = name => elements.find(element => value(element.opening, 'className') === `"${name}"`)
const insideDialog = element => element.parents.some(parent => parent.tagName.getText(tree) === 'dialog')

test('online/local radio choice stays in navigation, outside the settings dialog', () => {
  const options = byClass('orchestrator-options')
  assert.equal(options.opening.tagName.getText(tree), 'fieldset')
  assert.equal(insideDialog(options), false)
  assert.ok(options.parents.includes(byClass('sidebar').opening))
  assert.ok(elements.some(element => value(element.opening, 'type') === '"radio"' &&
    element.parents.includes(options.opening)))
})

test('one settings entry replaces the workspace header on every screen size', () => {
  const openers = elements.filter(element => element.opening.tagName.getText(tree) === 'button' &&
    value(element.opening, 'aria-controls') === '"backend-settings-panel"')
  assert.equal(openers.length, 1)
  assert.ok(openers[0].parents.includes(byClass('sidebar').opening))
  assert.equal(byClass('conversation-header'), undefined)
  assert.equal(byClass('orchestrator-bar'), undefined)
  assert.equal(insideDialog(byClass('conversation-identity')), true)
  const css = readFileSync(new URL('../src/style.css', import.meta.url), 'utf8')
  assert.doesNotMatch(css, /\.settings-nav-button\s*\{[^}]*display:\s*none/)
})

test('document management stays next to the composer, not above the transcript', () => {
  const sources = elements.find(element => element.opening.tagName.getText(tree) === 'KnowledgeSources')
  assert.ok(sources.parents.includes(byClass('composer-area').opening))
  const note = elements.find(element => value(element.opening, 'id') === '"upload-note"')
  assert.doesNotMatch(note.node.getText(tree), /Details in settings/)
})

test('verbose orchestration and file explanations live in the native settings dialog', () => {
  for (const id of ['orchestration-settings-title', 'file-settings-title']) {
    const heading = elements.find(element => value(element.opening, 'id') === `"${id}"`)
    assert.ok(heading)
    assert.equal(insideDialog(heading), true)
  }
  const dialog = elements.find(element => element.opening.tagName.getText(tree) === 'dialog')
  assert.match(dialog.node.getText(tree), /orchestrator\.description/)
  assert.match(dialog.node.getText(tree), /not verified/)
  assert.ok(attribute(dialog.opening, 'onCancel'))
})

test('mobile history uses a controlled native disclosure with a separate content region', () => {
  const history = byClass('chat-history')
  assert.equal(history.opening.tagName.getText(tree), 'details')
  assert.equal(value(history.opening, 'open'), '{historyOpen}')
  assert.ok(attribute(history.opening, 'onToggle'))
  assert.match(value(history.opening, 'onKeyDown'), /Escape/)
  assert.ok(byClass('history-content').parents.includes(history.opening))
})

test('mobile CSS reserves transcript space and bounds history independently', () => {
  const css = readFileSync(new URL('../src/style.css', import.meta.url), 'utf8')
  const mobile = css.slice(css.indexOf('@media (max-width: 760px)'))
  assert.match(mobile, /\.conversation-frame\s*\{[^}]*min-height:\s*min\(240px,\s*30dvh\)/)
  assert.match(mobile, /\.chat-history\[open\] \.history-content\s*\{[^}]*position:\s*absolute;[^}]*max-height:\s*40dvh;[^}]*overflow-y:\s*auto/)
  assert.doesNotMatch(mobile, /\.chat-app\s*\{[^}]*overflow:\s*hidden/)
})

test('Graph action cards render inside the assistant reply as plain text with accessible live status', () => {
  const cards = elements.find(element => element.opening.tagName.getText(tree) === 'ActionCards')
  assert.ok(cards.parents.some(parent => parent.tagName.getText(tree) === 'ReplyBody'))
  assert.ok(cards.parents.includes(byClass('assistant-content').opening))
  const component = readFileSync(new URL('../src/ActionCards.tsx', import.meta.url), 'utf8')
  assert.doesNotMatch(component, /dangerouslySetInnerHTML/)
  assert.match(component, /aria-live="polite"/)
  assert.match(component, /aria-busy=\{busy\}/)
  assert.match(component, /rel="noopener noreferrer"/)
  assert.match(component, /Nothing is changed until you confirm\./)
  assert.match(component, /aria-label=\{`Confirm: \$\{action\.title\}`\}/)
  const css = readFileSync(new URL('../src/style.css', import.meta.url), 'utf8')
  assert.match(css, /\.graph-action-details dd\s*\{[^}]*max-height:[^}]*overflow-y:\s*auto;[^}]*white-space:\s*pre-wrap/)
  const mobile = css.slice(css.indexOf('@media (max-width: 760px)'))
  assert.match(mobile, /\.graph-action-buttons button\s*\{[^}]*min-height:\s*44px/)
})
