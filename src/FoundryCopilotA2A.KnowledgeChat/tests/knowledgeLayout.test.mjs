import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

function jsx(filename) {
  const source = readFileSync(new URL(`../src/${filename}`, import.meta.url), 'utf8')
  const tree = ts.createSourceFile(filename, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const elements = []
  function visit(node, parents = []) {
    const opening = ts.isJsxElement(node) ? node.openingElement : ts.isJsxSelfClosingElement(node) ? node : undefined
    if (opening) {
      elements.push({ node, opening, parents })
      parents = [...parents, opening]
    }
    ts.forEachChild(node, (child) => visit(child, parents))
  }
  visit(tree)
  const attribute = (element, name) => element.attributes.properties.find((property) =>
    ts.isJsxAttribute(property) && property.name.text === name)
  const tag = (element) => element.opening.tagName.getText(tree)
  return { source, tree, elements, attribute, tag }
}
const chat = jsx('KnowledgeChat.tsx')
const [shell] = chat.elements.filter((element) => chat.tag(element) === 'ChatShell')
/** The JSX elements passed through one ChatShell prop. */
const propElements = (name) => {
  const property = chat.attribute(shell.opening, name)
  assert.ok(property, `ChatShell receives ${name}`)
  return chat.elements.filter((element) => element.node.pos >= property.pos && element.node.end <= property.end)
}
const knowledgeCss = readFileSync(new URL('../src/knowledge.css', import.meta.url), 'utf8')
const mobile = knowledgeCss.slice(knowledgeCss.indexOf('@media (max-width: 760px)'))

test('document management stays next to the composer, not above the transcript', () => {
  const [sources] = propElements('composerAccessory')
  assert.equal(chat.tag(sources), 'KnowledgeSources')
  assert.equal(chat.attribute(sources.opening, 'key').initializer.getText(chat.tree), '{contextId}')
  assert.equal(chat.elements.filter((element) => chat.tag(element) === 'KnowledgeSources').length, 1)
})

test('Documents & SharePoint opens a native dialog listing this chat documents', () => {
  const sources = jsx('KnowledgeSources.tsx')
  const dialogs = sources.elements.filter((element) => sources.tag(element) === 'dialog')
  assert.ok(dialogs.length >= 1)
  assert.ok(dialogs.every((dialog) => sources.attribute(dialog.opening, 'onCancel')))
  assert.ok(sources.elements.some((element) => sources.tag(element) === 'section' &&
    sources.attribute(element.opening, 'aria-label')?.initializer.getText(sources.tree) === '"Chat documents"'))
})

test('verbose orchestration and file explanations are settings sections, rendered in the shared dialog', () => {
  const sections = propElements('settings').filter((element) => chat.tag(element) === 'SettingsSection')
  assert.deepEqual(sections.map((section) => chat.attribute(section.opening, 'id').initializer.getText(chat.tree)),
    ['"orchestration-settings-title"', '"file-settings-title"'])
  assert.match(sections[0].node.getText(chat.tree), /Microsoft Agent Framework/)
  assert.match(sections[0].node.getText(chat.tree), /Azure-hosted model, not an offline LLM/)
  assert.ok(propElements('settings').some((element) => chat.tag(element) === 'SourceSettings'))
})

test('Graph action cards render inside the assistant reply as plain text with accessible live status', () => {
  const [cards] = propElements('renderTurnExtras')
  assert.equal(chat.tag(cards), 'ActionCards')
  assert.match(chat.attribute(cards.opening, 'onDecide').initializer.getText(chat.tree), /resolveGraphAction\(session, endpoint/)
  const component = readFileSync(new URL('../src/ActionCards.tsx', import.meta.url), 'utf8')
  assert.doesNotMatch(component, /dangerouslySetInnerHTML/)
  assert.match(component, /aria-live="polite"/)
  assert.match(component, /aria-busy=\{busy\}/)
  assert.match(component, /rel="noopener noreferrer"/)
  assert.match(component, /Nothing is changed until you confirm\./)
  assert.match(component, /aria-label=\{`Confirm: \$\{action\.title\}`\}/)
  assert.match(knowledgeCss, /\.graph-action-details dd\s*\{[^}]*max-height:[^}]*overflow-y:\s*auto;[^}]*white-space:\s*pre-wrap/)
  assert.match(mobile, /\.graph-action-buttons button\s*\{[^}]*min-height:\s*44px/)
})

test('Knowledge Chat loads the shared styles plus its own, and never the A2A transport', () => {
  const main = readFileSync(new URL('../src/main.tsx', import.meta.url), 'utf8')
  assert.match(main, /chat-shared\/style\.css/)
  assert.match(main, /knowledge\.css/)
  for (const name of readdirSync(new URL('../src/', import.meta.url)).filter((file) => /\.tsx?$/.test(file))) {
    const source = readFileSync(new URL(`../src/${name}`, import.meta.url), 'utf8')
    assert.doesNotMatch(source, /a2aTransport|FoundryCopilotA2A\.Chatbot\//, name)
  }
})
