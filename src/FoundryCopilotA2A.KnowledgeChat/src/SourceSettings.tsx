import { useRef, useState, type Dispatch, type KeyboardEvent, type ReactNode, type SetStateAction } from 'react'
import { availableKnowledgeSources, groupKnowledgeSources, knowledgeSourceOptions, selectedKnowledgeAgents, selectedKnowledgeSources,
  setKnowledgeAgent, setKnowledgeSource, toggleAllKnowledgeSources, type KnowledgeSelectionState,
  type KnowledgeSourceGroup } from './knowledgeSelection.ts'
import type { KnowledgeAgent } from './knowledgeClient.ts'
import type { KnowledgeCapabilityState } from './useKnowledgeCapabilities.ts'

const GROUP_NOTES: Record<string, string> = {
  'Microsoft 365': 'Reads with your delegated permissions. Changes are only proposed, and written after you confirm them.',
  Reference: 'Shared, read-only search indexes.',
  Search: 'Shared, read-only search indexes.',
  Create: 'Builds Word and PowerPoint files you can download or save to OneDrive.',
  Agents: 'Approved A2A specialists published through API Management, called with their configured access.',
}

interface SettingsTab { id: string; label: string; group?: KnowledgeSourceGroup }

/**
 * Which tools the local orchestrator may use, one tab per tool group so the panel fits the
 * viewport. `children` (general notes) become a trailing General tab.
 */
export default function SourceSettings({ selection, onChange, capabilityState, disabled, children }: {
  selection: KnowledgeSelectionState
  onChange: Dispatch<SetStateAction<KnowledgeSelectionState>>
  capabilityState: KnowledgeCapabilityState
  disabled: boolean
  children?: ReactNode
}) {
  const { capabilities, agents, agentsError, error } = capabilityState
  const availableSources = availableKnowledgeSources(capabilities)
  const activeSources = selectedKnowledgeSources(selection.sources, capabilities)
  const apimAgents = capabilities?.a2a ? agents : undefined
  const activeAgents = apimAgents && activeSources.includes('a2a') ? selectedKnowledgeAgents(apimAgents, selection.excludedAgents) : []
  const tabs: SettingsTab[] = [
    ...groupKnowledgeSources(knowledgeSourceOptions(capabilities)).map(group => ({ id: `tools-${group.id}`, label: group.label, group })),
    ...(children ? [{ id: 'general', label: 'General' }] : []),
  ]
  const [activeTab, setActiveTab] = useState<string>()
  const current = tabs.find(tab => tab.id === activeTab) ?? tabs[0]
  const tabButtons = useRef(new Map<string, HTMLButtonElement>())

  function moveFocus(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const next = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: tabs.length - 1 }[event.key]
    if (next === undefined) return
    event.preventDefault()
    const tab = tabs[(next + tabs.length) % tabs.length]
    setActiveTab(tab.id)
    const button = tabButtons.current.get(tab.id)
    button?.focus()
    button?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }

  return (
    <section className="settings-section settings-tools" aria-labelledby="source-settings-title">
      <div className="settings-tools-header">
        <div className="settings-tools-title">
          <h3 id="source-settings-title">Tools</h3>
          <span className="settings-tool-counter">{activeSources.length}/{availableSources.length} on</span>
          <button type="button" className="text-button settings-toggle-all" disabled={disabled}
            onClick={() => onChange((previous) => ({ ...previous, sources: toggleAllKnowledgeSources(previous.sources, capabilities), excludedAgents: [] }))}>
            {activeSources.length === availableSources.length ? 'Clear all' : 'Select all'}
          </button>
        </div>
        <p>Selected tools are allowed, not necessarily called. With none selected, document retrieval and general answers still work.</p>
      </div>

      <div className="settings-tabs" role="tablist" aria-label="Tool groups">
        {tabs.map((tab, index) => {
          const selected = tab.id === current?.id
          return (
            <button key={tab.id} ref={(button) => { if (button) tabButtons.current.set(tab.id, button); else tabButtons.current.delete(tab.id) }}
              type="button" role="tab" id={`settings-tab-${tab.id}`} className="settings-tab"
              aria-selected={selected} aria-controls={`settings-tabpanel-${tab.id}`} tabIndex={selected ? 0 : -1}
              onClick={(event) => { setActiveTab(tab.id); event.currentTarget.scrollIntoView({ block: 'nearest', inline: 'nearest' }) }}
              onKeyDown={(event) => moveFocus(event, index)}>
              {tab.label}
            </button>
          )
        })}
      </div>

      {current && (
        <div className="settings-tabpanel" role="tabpanel" id={`settings-tabpanel-${current.id}`} aria-labelledby={`settings-tab-${current.id}`}>
          {current.group
            ? <ToolGroup group={current.group} availableSources={availableSources} activeSources={activeSources}
              apimAgents={apimAgents} activeAgents={activeAgents} agentsError={agentsError} disabled={disabled} onChange={onChange} />
            : children}
        </div>
      )}
      {error && <p className="settings-capability-note" role="status">{error}</p>}
    </section>
  )
}

function ToolGroup({ group, availableSources, activeSources, apimAgents, activeAgents, agentsError, disabled, onChange }: {
  group: KnowledgeSourceGroup
  availableSources: string[]
  activeSources: string[]
  apimAgents?: KnowledgeAgent[]
  activeAgents: string[]
  agentsError?: string
  disabled: boolean
  onChange: Dispatch<SetStateAction<KnowledgeSelectionState>>
}) {
  const note = GROUP_NOTES[group.label]
  const showAgents = group.options.some(option => option.id === 'a2a') && availableSources.includes('a2a')
  return <>
    {note && <p className="settings-group-note">{note}</p>}
    <div className="settings-sources-grid">
      {group.options.map((option) => {
        const available = availableSources.includes(option.id)
        const checked = activeSources.includes(option.id)
        const hint = !available ? 'Unavailable · not enabled by the Knowledge API'
          : option.id === 'a2a' && apimAgents?.length
            ? `${checked ? activeAgents.length : 0} of ${apimAgents.length} approved agents selected`
            : option.hint
        return (
          <label key={option.id} className="settings-source-item" data-checked={checked} data-disabled={disabled || !available}>
            <input type="checkbox" checked={checked} disabled={disabled || !available}
              onChange={(event) => onChange((previous) => setKnowledgeSource(previous, option.id, event.target.checked))} />
            <div className="settings-source-text">
              <span className="settings-source-label">{option.label}</span>
              <small className="settings-source-hint">{hint}</small>
            </div>
          </label>
        )
      })}
    </div>
    {showAgents && (
      <fieldset className="settings-agents" disabled={disabled}>
        <legend>Approved agents</legend>
        {apimAgents === undefined
          ? <p className="settings-agents-empty" role="status">{agentsError ?? 'Loading approved agents…'}</p>
          : apimAgents.length === 0
            ? <p className="settings-agents-empty" role="status">No approved APIM agents are available right now.</p>
            : <ul className="settings-agents-list">
              {apimAgents.map((agent) => {
                const checked = activeAgents.includes(agent.id)
                return (
                  <li key={agent.id}>
                    <label className="settings-agent-item" data-checked={checked}>
                      <input type="checkbox" checked={checked}
                        onChange={(event) => onChange((previous) => setKnowledgeAgent(previous, apimAgents, agent.id, event.target.checked))} />
                      <span className="settings-agent-text">
                        <span className="settings-source-label">{agent.displayName}</span>
                        <code className="settings-agent-id">{agent.id}</code>
                        {agent.description && <small className="settings-source-hint">{agent.description}</small>}
                      </span>
                    </label>
                  </li>
                )
              })}
            </ul>}
      </fieldset>
    )}
  </>
}
