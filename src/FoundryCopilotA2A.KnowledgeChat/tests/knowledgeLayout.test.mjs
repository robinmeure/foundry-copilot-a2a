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

test('local originals have a OneDrive upload button on the chat document row with inline status', () => {
  const sources = jsx('KnowledgeSources.tsx')
  const [chip] = sources.elements.filter(element => sources.tag(element) === 'DocumentChip')
  assert.ok(chip)
  const actions = sources.attribute(chip.opening, 'actions')
  assert.ok(actions)
  assert.match(actions.getText(sources.tree), /Upload to OneDrive/)
  assert.match(actions.getText(sources.tree), /uploadToOneDrive\(document\)/)
  assert.match(actions.getText(sources.tree), /Open in OneDrive/)
  assert.match(sources.attribute(chip.opening, 'feedback').getText(sources.tree), /'alert' : 'status'/)
  assert.match(sources.source, /oneDriveUploadExpiresAt/)
  assert.match(knowledgeCss, /\.document-chip-actions\s*\{[^}]*display:\s*flex/)
  assert.match(mobile, /\.document-onedrive-button\s*\{[^}]*min-height:\s*44px/)
})

test('verbose orchestration and file explanations are settings sections, rendered in the shared dialog', () => {
  const sections = propElements('settings').filter((element) => chat.tag(element) === 'SettingsSection')
  assert.deepEqual(sections.map((section) => chat.attribute(section.opening, 'id').initializer.getText(chat.tree)),
    ['"orchestration-settings-title"', '"file-settings-title"'])
  assert.match(sections[0].node.getText(chat.tree), /Microsoft Agent Framework/)
  assert.match(sections[0].node.getText(chat.tree), /Azure-hosted model, not an offline LLM/)
  assert.ok(propElements('settings').some((element) => chat.tag(element) === 'SourceSettings'))
})

test('tool settings are tabbed per tool group with the general notes in a trailing tab', () => {
  const [settings] = propElements('settings').filter((element) => chat.tag(element) === 'SourceSettings')
  const nested = propElements('settings').filter((element) => element.parents.includes(settings.opening))
  assert.ok(nested.some((element) => chat.tag(element) === 'SettingsSection'), 'general sections render inside the tabs')
  const tools = jsx('SourceSettings.tsx')
  const role = (value) => tools.elements.find((element) =>
    tools.attribute(element.opening, 'role')?.initializer.getText(tools.tree) === `"${value}"`)
  assert.ok(role('tablist') && role('tab') && role('tabpanel'))
  const tab = role('tab').opening
  for (const name of ['aria-selected', 'aria-controls', 'tabIndex', 'onKeyDown']) assert.ok(tools.attribute(tab, name), name)
  assert.match(tools.source, /groupKnowledgeSources/)
  assert.match(knowledgeCss, /\.settings-tabs\s*\{[^}]*overflow-x:\s*auto/)
  assert.match(mobile, /\.settings-tab\s*\{[^}]*min-height:\s*44px/)
})

test('Graph action cards render inside the assistant reply as plain text with accessible live status', () => {
  const elements = propElements('renderTurnExtras')
  const cards = elements.find(element => chat.tag(element) === 'ActionCards')
  assert.ok(cards)
  assert.match(chat.attribute(cards.opening, 'onDecide').initializer.getText(chat.tree), /resolveGraphAction\(session, endpoint/)
  const files = elements.find(element => chat.tag(element) === 'GeneratedFiles')
  assert.ok(files)
  assert.match(chat.attribute(files.opening, 'onDownload').initializer.getText(chat.tree),
    /downloadGeneratedFile\(endpoint\.baseUrl, contextId, file, endpoint\.token\)/)
  assert.ok(elements.indexOf(files) < elements.indexOf(cards))
  const component = readFileSync(new URL('../src/ActionCards.tsx', import.meta.url), 'utf8')
  assert.doesNotMatch(component, /dangerouslySetInnerHTML/)
  assert.match(component, /aria-live="polite"/)
  assert.match(component, /aria-busy=\{busy\}/)
  assert.match(component, /rel="noopener noreferrer"/)
  assert.match(component, /Nothing is changed until you confirm\./)
  assert.match(component, /aria-label=\{`Confirm: \$\{action\.title\}`\}/)
  assert.match(component, /actionConfirmLabel\(action\.kind\)/)
  assert.match(component, /case 'mail\.draft': return 'Create draft'/)
  assert.match(component, /case 'files\.save': return 'Save to OneDrive'/)
  assert.match(component, /'graph-action-detail-body'/)
  assert.match(component, /aria-label=\{`Cancel: \$\{action\.title\}`\}/)
  assert.match(component, />Dismiss</)
  assert.match(knowledgeCss, /\.graph-action\[data-kind\^="mail\."\].*graph-action-detail-body/)
  assert.match(knowledgeCss, /\.graph-action-footer\s*\{[^}]*justify-content:\s*space-between/)
  assert.doesNotMatch(knowledgeCss, /\.graph-action\s*\{[^}]*border-left:\s*3px/)
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
