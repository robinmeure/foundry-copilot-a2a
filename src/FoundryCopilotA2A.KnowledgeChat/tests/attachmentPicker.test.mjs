import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

function jsx(filename) {
  const source = readFileSync(new URL(`../src/${filename}`, import.meta.url), 'utf8')
  const tree = ts.createSourceFile(filename, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const nodes = []
  function visit(node) {
    if (ts.isJsxElement(node)) nodes.push({ node, opening: node.openingElement })
    if (ts.isJsxSelfClosingElement(node)) nodes.push({ node, opening: node })
    ts.forEachChild(node, visit)
  }
  visit(tree)
  const attr = (element, name) => element.opening.attributes.properties.find(property =>
    ts.isJsxAttribute(property) && property.name.text === name)?.initializer?.getText(tree)
  return { tree, source, nodes, attr }
}

test('attachment icon opens a native popover with local and SharePoint choices', () => {
  const { tree, source, nodes, attr } = jsx('AttachmentMenu.tsx')
  const button = nodes.find(node => attr(node, 'aria-label') === '"Attach a file"')
  assert.equal(button.opening.tagName.getText(tree), 'button')
  assert.ok(attr(button, 'popoverTarget'))
  assert.equal(attr(button, 'aria-haspopup'), '"menu"')
  assert.ok(nodes.some(node => attr(node, 'popover') === '"auto"' && attr(node, 'role') === '"menu"'))
  const items = nodes.filter(node => attr(node, 'role') === '"menuitem"')
  assert.equal(items.length, 2)
  assert.match(items[0].node.getText(tree), /Local file/)
  assert.match(items[1].node.getText(tree), /SharePoint/)
  assert.match(attr(items[1], 'disabled'), /!sharePointAvailable/)
  assert.match(source, /ArrowDown.*ArrowUp.*Home.*End/)
  assert.match(source, /hidePopover/)
})

test('SharePoint uses a labelled native modal with selection and explicit import', () => {
  const { tree, source, nodes, attr } = jsx('KnowledgeSources.tsx')
  const dialog = nodes.find(node => node.opening.tagName.getText(tree) === 'dialog')
  assert.equal(attr(dialog, 'aria-labelledby'), '"sharepoint-picker-title"')
  assert.ok(attr(dialog, 'onCancel'))
  assert.match(source, /modal\.showModal\(\)/)
  assert.match(source, /button\?\.focus\(\)/)
  assert.ok(nodes.some(node => attr(node, 'name') === '"sharepoint-file"' && attr(node, 'type') === '"radio"'))
  const add = nodes.find(node => attr(node, 'className') === '"primary-button"')
  assert.match(attr(add, 'disabled'), /blocked \|\| !selected/)
  assert.match(attr(add, 'onClick'), /run\('import'/)
  assert.match(attr(add, 'onClick'), /driveId: drive, itemId: file\.id/)
  assert.match(source, /if \(busy === 'import'\) return/)
  assert.doesNotMatch(source, /<summary>Documents &amp; SharePoint/)
})

test('attachment choices preserve the native local chooser, and SharePoint is always offered by Knowledge Chat', () => {
  const { tree, nodes, attr } = jsx('useKnowledgeDocumentAttachment.tsx')
  const menu = nodes.find(node => node.opening.tagName.getText(tree) === 'AttachmentMenu')
  assert.match(attr(menu, 'onLocalFile'), /input\.current\?\.click\(\)/)
  assert.equal(attr(menu, 'sharePointAvailable'), undefined, 'a boolean shorthand attribute has no initializer')
  assert.ok(menu.opening.attributes.properties.some(property => ts.isJsxAttribute(property) && property.name.text === 'sharePointAvailable'))
  assert.equal(attr(menu, 'disabled'), '{disabled}')
  assert.match(attr(menu, 'onSharePoint'), /setPickerOpen\(true\)/)
  const chat = jsx('KnowledgeChat.tsx')
  assert.match(chat.source, /useKnowledgeDocumentAttachment\(!ready \|\| isSending, contextId\)/)
  const picker = chat.nodes.find(node => node.opening.tagName.getText(chat.tree) === 'KnowledgeSources')
  assert.equal(chat.attr(picker, 'pickerOpen'), '{attachment.pickerOpen}')
})

test('local documents show named chips for pending, indexing and available states', () => {
  const chat = jsx('useKnowledgeDocumentAttachment.tsx')
  const pending = chat.nodes.find(node => node.opening.tagName.getText(chat.tree) === 'DocumentChip' &&
    chat.attr(node, 'state') === '"pending"')
  const indexing = chat.nodes.find(node => node.opening.tagName.getText(chat.tree) === 'DocumentChip' &&
    chat.attr(node, 'state') === '"indexing"')
  assert.equal(chat.attr(pending, 'name'), '{document.name}')
  assert.ok(chat.attr(pending, 'onRemove'))
  assert.equal(chat.attr(indexing, 'name'), '{indexing.file.name}')
  assert.doesNotMatch(chat.source, /Selected for backend ingestion|Remove selected document/)
  assert.match(chat.source, /upload\?\.contextId === contextId/)
  const sources = jsx('KnowledgeSources.tsx')
  assert.ok(sources.nodes.some(node => node.opening.tagName.getText(sources.tree) === 'section' &&
    sources.attr(node, 'aria-label') === '"Chat documents"'))
  assert.ok(sources.nodes.some(node => node.opening.tagName.getText(sources.tree) === 'DocumentChip' &&
    sources.attr(node, 'state') === '"ready"'))
  const chip = jsx('DocumentChip.tsx')
  assert.match(chip.source, /Ready to add/)
  assert.match(chip.source, /Available for answers/)
})

test('SharePoint picker supports URLs and only auto-opens a server-resolved site', () => {
  const { source, nodes, attr } = jsx('KnowledgeSources.tsx')
  const input = nodes.find(node => attr(node, 'id') === '"sharepoint-site-query"')
  assert.equal(attr(input, 'placeholder'), '"Site name or SharePoint site URL"')
  assert.match(source, /kind === 'sites' && !continuation && page\.resolvedSiteId/)
  assert.match(source, /setSite\(page\.resolvedSiteId\)/)
  assert.match(source, /await browse\('drives', page\.resolvedSiteId, '', signal\)/)
  assert.match(source, /if \(signal\.aborted\) return/)
  assert.match(source, /if \(continuation\) params\.set\('nextPage', continuation\)/)
})
