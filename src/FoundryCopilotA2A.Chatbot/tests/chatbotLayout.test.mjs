import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

const read = (name) => readFileSync(new URL(`../src/${name}`, import.meta.url), 'utf8')
const sourceFiles = readdirSync(new URL('../src/', import.meta.url)).filter((file) => /\.tsx?$/.test(file))
function imports(name) {
  const tree = ts.createSourceFile(name, read(name), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  return tree.statements.filter(ts.isImportDeclaration).map((statement) => statement.moduleSpecifier.text)
}

test('the APIM chatbot imports nothing from the Knowledge API chat', () => {
  for (const name of sourceFiles) {
    for (const specifier of imports(name)) {
      assert.doesNotMatch(specifier, /knowledge|KnowledgeChat|graphActions|orchestrator|SharePoint|ActionCards/i, `${name} imports ${specifier}`)
    }
    assert.doesNotMatch(read(name), /VITE_KNOWLEDGE_/, name)
  }
})

test('the APIM chatbot uses the shared shell without knowledge accessories', () => {
  const source = read('Chatbot.tsx')
  assert.match(source, /<ChatShell /)
  assert.doesNotMatch(source, /composerAccessory|renderTurnExtras|orchestrator-options|type="radio"/)
  assert.match(source, /createA2ATransport\(/)
})

test('agent and file explanations live in the shared settings dialog sections', () => {
  const source = read('Chatbot.tsx')
  const settings = source.slice(source.indexOf('settings={{'))
  assert.match(settings, /<SettingsSection id="agent-settings-title" title="Agent">/)
  assert.match(settings, /<SettingsSection id="file-settings-title" title="Files">[\s\S]*not verified/)
})
