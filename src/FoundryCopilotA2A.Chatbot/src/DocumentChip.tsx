import ChatIcon from './ChatIcon.tsx'

const labels = { pending: 'Ready to add', indexing: 'Indexing', ready: 'Available for answers' }

export default function DocumentChip({ name, detail, state, onRemove, disabled = false }: {
  name: string; detail: string; state: keyof typeof labels; onRemove?: () => void; disabled?: boolean
}) {
  const extension = (name.includes('.') ? name.split('.').at(-1)?.toUpperCase() : undefined) || 'FILE'
  return <div className="document-chip" data-state={state} role="group" aria-label={`${name}: ${labels[state]}`}
    aria-busy={state === 'indexing'}>
    <span className="document-chip-type" aria-hidden="true"><ChatIcon name="file" /><span>{extension}</span></span>
    <div className="document-chip-copy">
      <strong>{name}</strong>
      <div className="document-chip-detail">
        <span className="document-chip-state"><ChatIcon name={state === 'ready' ? 'check' : state === 'indexing' ? 'sparkle' : 'attach'} />{labels[state]}</span>
        <span>{detail}</span>
      </div>
    </div>
    {onRemove && <button type="button" className="icon-button" disabled={disabled}
      aria-label={`Remove ${name}`} title="Remove document" onClick={onRemove}><ChatIcon name="close" /></button>}
  </div>
}
