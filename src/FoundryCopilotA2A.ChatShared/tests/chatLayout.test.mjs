import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

// Structural guards for the shared chat layout; each app supplies only its own sections and accessories.
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
  const value = (element, name) => attribute(element, name)?.initializer?.getText(tree)
  const tag = (element) => element.opening.tagName.getText(tree)
  const byClass = (name) => elements.find((element) => value(element.opening, 'className') === `"${name}"`)
  const byTag = (name) => elements.filter((element) => tag(element) === name)
  return { source, tree, elements, attribute, value, tag, byClass, byTag }
}
const css = readFileSync(new URL('../src/style.css', import.meta.url), 'utf8')
const mobile = css.slice(css.indexOf('@media (max-width: 760px)'))

test('one settings entry, titled Settings, replaces the workspace header on every screen size', () => {
  const sidebar = jsx('Sidebar.tsx')
  assert.match(sidebar.source, /export const settingsPanelId = 'backend-settings-panel'/)
  const openers = sidebar.byTag('button').filter((element) => sidebar.value(element.opening, 'aria-controls') === '{settingsPanelId}')
  assert.equal(openers.length, 1)
  const [opener] = openers
  assert.equal(sidebar.value(opener.opening, 'title'), '"Settings"')
  assert.equal(sidebar.value(opener.opening, 'aria-haspopup'), '"dialog"')
  assert.ok(opener.parents.includes(sidebar.byClass('sidebar').opening))

  const shell = jsx('ChatShell.tsx')
  assert.equal(shell.elements.filter((element) => shell.attribute(element.opening, 'aria-controls')).length, 0,
    'the workspace has no second settings opener')
  assert.equal(shell.byClass('conversation-header'), undefined)
  assert.equal(shell.byClass('orchestrator-bar'), undefined)
  assert.doesNotMatch(css, /\.settings-nav-button\s*\{[^}]*display:\s*none/)
})

test('settings use a native dialog that closes on Escape and holds the conversation identity and app sections', () => {
  const dialog = jsx('SettingsDialog.tsx')
  const [native] = dialog.byTag('dialog')
  assert.equal(dialog.value(native.opening, 'id'), '{settingsPanelId}')
  assert.match(dialog.value(native.opening, 'onCancel'), /preventDefault\(\)[\s\S]*onClose\(\)/)
  assert.equal(dialog.value(native.opening, 'ref'), '{dialog}')
  assert.match(dialog.source, /const dialog = useModalDialog\(true\)/)
  assert.match(readFileSync(new URL('../src/hooks.ts', import.meta.url), 'utf8'), /showModal\(\)/)
  const identity = dialog.byClass('conversation-identity')
  assert.equal(dialog.tag(identity), 'details')
  assert.ok(identity.parents.includes(native.opening))
  const children = dialog.elements.find((element) => element.node.getText(dialog.tree).includes('{children}') &&
    element.parents.includes(native.opening))
  assert.ok(children, 'app-specific sections render inside the dialog')

  const shell = jsx('ChatShell.tsx')
  const [settings] = shell.byTag('SettingsDialog')
  assert.match(settings.node.getText(shell.tree), /\{props\.settings\.sections\}/)
})

test('mobile history uses a controlled native disclosure with a separate content region', () => {
  const { tag, value, attribute, byClass } = jsx('Sidebar.tsx')
  const history = byClass('chat-history')
  assert.equal(tag(history), 'details')
  assert.equal(value(history.opening, 'open'), '{historyOpen}')
  assert.ok(attribute(history.opening, 'onToggle'))
  assert.match(value(history.opening, 'onKeyDown'), /Escape/)
  assert.ok(byClass('history-content').parents.includes(history.opening))
})

test('mobile CSS reserves transcript space and bounds history independently', () => {
  assert.match(mobile, /\.conversation-frame\s*\{[^}]*min-height:\s*min\(240px,\s*30dvh\)/)
  assert.match(mobile, /\.chat-history\[open\] \.history-content\s*\{[^}]*position:\s*absolute;[^}]*max-height:\s*40dvh;[^}]*overflow-y:\s*auto/)
  assert.doesNotMatch(mobile, /\.chat-app\s*\{[^}]*overflow:\s*hidden/)
})

test('app accessories sit next to the composer and turn extras render inside the assistant reply', () => {
  const shell = jsx('ChatShell.tsx')
  const area = shell.byClass('composer-area')
  assert.match(area.node.getText(shell.tree), /^<div className="composer-area">\s*\{props\.composerAccessory\}/)
  const note = shell.elements.find((element) => shell.value(element.opening, 'id') === '"upload-note"')
  assert.ok(note.parents.includes(area.opening))
  assert.doesNotMatch(note.node.getText(shell.tree), /Details in settings/)

  const transcript = jsx('Transcript.tsx')
  const [reply] = transcript.byTag('ReplyBody')
  assert.match(reply.node.getText(transcript.tree), /\{props\.renderTurnExtras\?\.\(turn\)\}/)
  assert.ok(reply.parents.includes(transcript.byClass('assistant-content').opening))
})

test('shared styles carry no app-specific Knowledge or Graph action rules', () => {
  assert.doesNotMatch(css, /\.graph-action|\.document-chip|\.sharepoint|\.settings-source/)
})
