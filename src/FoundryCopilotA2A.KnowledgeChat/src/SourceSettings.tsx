import type { Dispatch, SetStateAction } from 'react'
import { availableKnowledgeSources, knowledgeSourceOptions, selectedKnowledgeAgents, selectedKnowledgeSources,
  setKnowledgeAgent, setKnowledgeSource, toggleAllKnowledgeSources, type KnowledgeSelectionState } from './knowledgeSelection.ts'
import type { KnowledgeCapabilityState } from './useKnowledgeCapabilities.ts'

/** Which Microsoft 365 sources and APIM agents the local orchestrator may use. */
export default function SourceSettings({ selection, onChange, capabilityState, disabled }: {
  selection: KnowledgeSelectionState
  onChange: Dispatch<SetStateAction<KnowledgeSelectionState>>
  capabilityState: KnowledgeCapabilityState
  disabled: boolean
}) {
  const { capabilities, agents, agentsError, error } = capabilityState
  const availableSources = availableKnowledgeSources(capabilities)
  const activeSources = selectedKnowledgeSources(selection.sources, capabilities)
  const apimAgents = capabilities?.a2a ? agents : undefined
  const activeAgents = apimAgents && activeSources.includes('a2a') ? selectedKnowledgeAgents(apimAgents, selection.excludedAgents) : []
  return (
    <section className="settings-section" aria-labelledby="source-settings-title">
      <div className="settings-section-header">
        <div>
          <h3 id="source-settings-title">Available sources</h3>
          <p>
            Selected sources are allowed, not necessarily called. Graph tools read with delegated permissions and only propose changes, which are written after you confirm them; APIM agents use their configured access. Search sources query shared, read-only indexes.
            Clearing all leaves document retrieval and general model answers available.
          </p>
        </div>
        <div className="settings-section-actions">
          <span className="settings-tool-counter">{activeSources.length}/{availableSources.length} selected</span>
          <button type="button" className="text-button settings-toggle-all" disabled={disabled}
            onClick={() => onChange((previous) => ({ ...previous, sources: toggleAllKnowledgeSources(previous.sources, capabilities), excludedAgents: [] }))}>
            {activeSources.length === availableSources.length ? 'Clear all' : 'Select all'}
          </button>
        </div>
      </div>

      <div className="settings-sources-grid">
        {knowledgeSourceOptions(capabilities).map((option) => {
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
      {availableSources.includes('a2a') && (
        <fieldset className="settings-agents" disabled={disabled}>
          <legend>APIM agents</legend>
          <p className="settings-agents-hint">
            Approved A2A specialists published through API Management. The orchestrator may ask the checked agents read-only questions.
          </p>
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
      {error && <p className="settings-capability-note" role="status">{error}</p>}
    </section>
  )
}
