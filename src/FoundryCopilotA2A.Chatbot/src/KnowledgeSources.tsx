import { type RefObject, useEffect, useRef, useState } from 'react'
import ChatIcon from './ChatIcon.tsx'
import DocumentChip from './DocumentChip.tsx'
import { knowledgeRequest, type GraphItem, type GraphPage, type KnowledgeDocument, type KnowledgeToken } from './knowledgeClient.ts'

export default function KnowledgeSources({ baseUrl, conversation, token, disabled, revision,
  pickerOpen, onPickerClose, opener }: {
  baseUrl: string; conversation: string; token: KnowledgeToken; disabled: boolean; revision: number
  pickerOpen: boolean; onPickerClose: () => void; opener: RefObject<HTMLButtonElement | null>
}) {
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([])
  const [documentError, setDocumentError] = useState('')
  const [pickerError, setPickerError] = useState('')
  const [notice, setNotice] = useState('')
  const [reload, setReload] = useState(0)
  const [busy, setBusy] = useState<'browse' | 'import' | 'remove'>()
  const [query, setQuery] = useState('')
  const searchedQuery = useRef('')
  const [searched, setSearched] = useState(false)
  const [sites, setSites] = useState<GraphItem[]>([])
  const [drives, setDrives] = useState<GraphItem[]>([])
  const [items, setItems] = useState<GraphItem[]>([])
  const [site, setSite] = useState('')
  const [drive, setDrive] = useState('')
  const [folders, setFolders] = useState<GraphItem[]>([])
  const [selected, setSelected] = useState<GraphItem>()
  const [nextPage, setNextPage] = useState<string>()
  const [listing, setListing] = useState<'sites' | 'drives' | 'items'>('sites')
  const tokenRef = useRef(token)
  useEffect(() => { tokenRef.current = token }, [token])
  const controller = useRef<AbortController | undefined>(undefined)
  const dialog = useRef<HTMLDialogElement>(null)

  useEffect(() => () => controller.current?.abort(), [])

  useEffect(() => {
    const abort = new AbortController()
    void knowledgeRequest<KnowledgeDocument[]>(baseUrl, `conversations/${conversation}/documents`,
      claims => tokenRef.current(claims), { signal: abort.signal })
      .then(result => {
        if (!abort.signal.aborted) { setDocuments(result); setDocumentError('') }
      }).catch((reason: unknown) => {
        if (!abort.signal.aborted) setDocumentError(reason instanceof Error ? reason.message : 'Could not load documents.')
      })
    return () => abort.abort()
  }, [baseUrl, conversation, revision, reload])

  useEffect(() => {
    const modal = dialog.current
    if (!pickerOpen || !modal) return
    const button = opener.current
    modal.showModal()
    return () => { modal.close(); button?.focus() }
  }, [pickerOpen, opener])

  function closePicker() {
    if (busy === 'import') return
    if (busy === 'browse') {
      controller.current?.abort()
      controller.current = undefined
      setBusy(undefined)
    }
    onPickerClose()
  }

  async function run(kind: 'browse' | 'import' | 'remove', action: (signal: AbortSignal) => Promise<void>) {
    if (disabled || controller.current) return
    const abort = new AbortController()
    controller.current = abort
    setBusy(kind)
    const setError = kind === 'remove' ? setDocumentError : setPickerError
    setError('')
    setNotice('')
    try { await action(abort.signal) } catch (reason) {
      if (!abort.signal.aborted) setError(reason instanceof Error ? reason.message : 'The source operation failed. Try again.')
    } finally {
      if (controller.current === abort) {
        controller.current = undefined
        setBusy(undefined)
      }
    }
  }

  async function browse(kind: 'sites' | 'drives' | 'items', parent: string, driveId: string,
    signal: AbortSignal, continuation?: string) {
    const params = new URLSearchParams({ parent, drive: driveId, query: searchedQuery.current })
    if (continuation) params.set('nextPage', continuation)
    const page = await knowledgeRequest<GraphPage>(baseUrl, `graph/${kind}?${params}`, token, { signal })
    if (signal.aborted) return
    const set = kind === 'sites' ? setSites : kind === 'drives' ? setDrives : setItems
    set(previous => continuation ? [...previous, ...page.items] : page.items)
    setListing(kind)
    setNextPage(page.nextPage)
    if (kind === 'sites' && !continuation && page.resolvedSiteId) {
      setSite(page.resolvedSiteId)
      await browse('drives', page.resolvedSiteId, '', signal)
    }
  }

  const blocked = Boolean(busy) || disabled
  const importing = busy === 'import'
  return <>
    {documentError && <p className="turn-error" role="alert">{documentError}{' '}
      <button type="button" className="text-button" disabled={blocked} onClick={() => setReload(value => value + 1)}>Reload documents</button>
    </p>}
    {documents.length > 0 && <section className="knowledge-sources" aria-label="Chat documents">
      <div className="knowledge-sources-heading">Chat documents <span>{documents.length}</span></div>
      <p>Retrieved when relevant. Private to this conversation; expires after 7 days.</p>
        <ul>{documents.map(document => <li key={document.id}>
          <DocumentChip name={document.name} state="ready" detail={`${document.chunks} passages`} disabled={blocked}
            onRemove={() => void run('remove', async signal => {
              await knowledgeRequest(baseUrl, `conversations/${conversation}/documents/${document.id}`, token,
                { method: 'DELETE', signal })
              if (!signal.aborted) {
                setDocuments(previous => previous.filter(item => item.id !== document.id))
                setNotice(`Removed ${document.name} from this conversation.`)
              }
            })} />
        </li>)}</ul>
    </section>}
    <p className="sr-only" role="status">
      {busy === 'remove' ? 'Removing document...' : notice}
    </p>
    {pickerOpen && <dialog ref={dialog} className="sharepoint-picker" aria-labelledby="sharepoint-picker-title"
      aria-describedby="sharepoint-picker-description"
      onCancel={event => { event.preventDefault(); closePicker() }}>
      <div className="picker-header">
        <div>
          <h2 id="sharepoint-picker-title">Add from SharePoint</h2>
          <p id="sharepoint-picker-description">Choose a document to use in this conversation.</p>
        </div>
        <button type="button" className="icon-button" aria-label="Close SharePoint picker" disabled={importing}
          onClick={closePicker}><ChatIcon name="close" /></button>
      </div>
      <div className="picker-body">
        <form className="picker-search" onSubmit={event => {
          event.preventDefault()
          if (blocked) return
          searchedQuery.current = query.trim()
          setSite(''); setDrive(''); setSites([]); setDrives([]); setItems([]); setFolders([])
          setSelected(undefined); setNextPage(undefined); setListing('sites'); setSearched(false)
          void run('browse', async signal => {
            await browse('sites', '', '', signal)
            if (!signal.aborted) setSearched(true)
          })
        }}>
          <label className="sr-only" htmlFor="sharepoint-site-query">Site name or SharePoint site URL</label>
          <ChatIcon name="search" />
          <input id="sharepoint-site-query" type="search" value={query} onChange={event => setQuery(event.target.value)}
            placeholder="Site name or SharePoint site URL" autoFocus disabled={blocked} />
          <button type="submit" className="secondary-button" disabled={blocked}>Find site</button>
        </form>
        {sites.length > 0 && <div className="picker-location">
          <label>Site<select value={site} disabled={blocked} onChange={event => {
            const id = event.target.value
            setSite(id); setDrive(''); setDrives([]); setItems([]); setFolders([])
            setSelected(undefined); setNextPage(undefined); setListing('drives')
            if (id) void run('browse', signal => browse('drives', id, '', signal))
          }}><option value="">Choose a site</option>{sites.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
          <label>Library<select value={drive} disabled={blocked || !drives.length} onChange={event => {
            const id = event.target.value
            setDrive(id); setItems([]); setFolders([]); setSelected(undefined); setNextPage(undefined); setListing('items')
            if (id) void run('browse', signal => browse('items', '', id, signal))
          }}><option value="">Choose a library</option>{drives.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
        </div>}
        {drive && <div className="picker-path">
          <span>{drives.find(item => item.id === drive)?.name ?? 'Library'}{folders.map(folder => ` / ${folder.name}`).join('')}</span>
          {folders.length > 0 && <button type="button" className="text-button" disabled={blocked}
            onClick={() => void run('browse', async signal => {
              const parent = folders.slice(0, -1)
              await browse('items', parent.at(-1)?.id ?? '', drive, signal)
              if (!signal.aborted) { setFolders(parent); setSelected(undefined) }
            })}>Up one folder</button>}
        </div>}
        {pickerError && <p className="turn-error" role="alert">{pickerError}</p>}
        <div className="picker-files" aria-busy={Boolean(busy)} aria-label="SharePoint files">
          {busy === 'browse' && <p className="picker-empty" role="status">Loading SharePoint...</p>}
          {!busy && !pickerError && items.length === 0 && <p className="picker-empty">
            {!searched ? 'Search by site name or paste a SharePoint site URL to open its document libraries.'
              : !sites.length ? 'No sites found. Try a different site name.'
              : !site ? 'Choose a site to see its document libraries.'
              : !drives.length ? 'No document libraries are available in this site.'
              : !drive ? 'Choose a document library to browse files.'
              : 'This folder is empty.'}
          </p>}
          {items.length > 0 && <ul aria-label="Files and folders">
            {items.map(item => <li key={item.id}>
              {item.folder ? <button type="button" className="picker-folder" disabled={blocked}
                onClick={() => void run('browse', async signal => {
                  await browse('items', item.id, drive, signal)
                  if (!signal.aborted) { setFolders(previous => [...previous, item]); setSelected(undefined) }
                })}><ChatIcon name="folder" /><span>{item.name}</span><ChatIcon name="chevron-down" /></button>
                : <label className="picker-file" data-selected={selected?.id === item.id}>
                  <input type="radio" name="sharepoint-file" value={item.id} checked={selected?.id === item.id}
                    disabled={blocked} onChange={() => setSelected(item)} />
                  <ChatIcon name="file" /><span>{item.name}</span>
                </label>}
            </li>)}
          </ul>}
        </div>
        {nextPage && <button type="button" className="text-button picker-load-more" disabled={blocked}
          onClick={() => void run('browse', signal => browse(listing,
            listing === 'drives' ? site : folders.at(-1)?.id ?? '', drive, signal, nextPage))}>Load more</button>}
        <p className="picker-privacy">Files are copied to the conversation index and expire after 7 days. PDF, DOCX, TXT, Markdown or CSV; up to 20 MiB / 300 pages.</p>
      </div>
      <div className="picker-footer">
        <span role="status">{importing ? 'Adding document... Indexing can take several minutes.' : selected?.name ?? 'No file selected'}</span>
        <div>
          <button type="button" className="secondary-button" disabled={importing} onClick={closePicker}>Cancel</button>
          <button type="button" className="primary-button" disabled={blocked || !selected}
            onClick={() => {
              if (!selected) return
              const file = selected
              void run('import', async signal => {
                const imported = await knowledgeRequest<KnowledgeDocument>(baseUrl,
                  `conversations/${conversation}/sharepoint`, token, { method: 'POST', signal,
                    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ driveId: drive, itemId: file.id }) })
                if (!signal.aborted) {
                  setDocuments(previous => [...previous.filter(item => item.id !== imported.id), imported])
                  setNotice(`Added ${imported.name} to this conversation.`)
                  setSelected(undefined)
                  onPickerClose()
                }
              })
            }}>{importing ? 'Adding...' : 'Add to conversation'}</button>
        </div>
      </div>
    </dialog>}
  </>
}
