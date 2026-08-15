import { useEffect, useState } from 'react'

import AlertIcon from '../../components/AlertIcon'
import RecordTable, { type RecordColumn } from '../../components/RecordTable'
import { listLinks, type AdminLink, type GlobalLimits, type LinksPage } from './adminLinks'
import { asApiError } from '../../lib/api'
import LinkEditor from './LinkEditor'

// The two status shapes kairos.css draws, and the Second Channel Rule is why
// they are shapes: `live` fills the 7px dot, `paused` leaves it hollow at the
// same size. The state survives a monochrome screenshot and a colour-blind
// reader, and the word beside the dot says which is which regardless.
const LIVE = 'live'
const PAUSED = 'paused'

/** Nothing on this page can fix a missing session, so it says so and stops. */
const UNAUTHORIZED = 401
const LOCKED_MESSAGE =
  'This session has expired or was never signed in. Open /admin/login and come back.'

type Phase = 'loading' | 'ready' | 'broken'

/**
 * The editor's subject: an existing link, or `null` meaning a new one. Wrapped
 * in an object so "editing a new link" and "not editing" stay distinguishable —
 * two nulls would be one state.
 */
type Editing = { link: AdminLink | null }

/**
 * A margin cell that says where its number came from.
 *
 * An inherited 15 and an overridden 15 are the same digits, so the difference
 * has to be in the words. This is the list's half of the same distinction
 * InheritableNumber draws in the form.
 */
function margin(link: AdminLink, globals: GlobalLimits) {
  if (link.marginMinutes === null) {
    return <span className="muted numeric t-xs">Global — {globals.marginMinutes} min</span>
  }

  return `${link.marginMinutes} min`
}

/** The operator's list of bookable event types, and the way into editing one. */
export default function Links() {
  const [page, setPage] = useState<LinksPage | null>(null)
  const [phase, setPhase] = useState<Phase>('loading')
  const [failure, setFailure] = useState('')
  const [editing, setEditing] = useState<Editing | null>(null)
  // Bumped after every write. The page re-reads rather than patching its own
  // copy, because the server decides the slug, the order and what a limit
  // actually ended up as — and a local patch is where those two drift apart.
  const [reload, setReload] = useState(0)

  useEffect(() => {
    const controller = new AbortController()

    listLinks(controller.signal)
      .then((read) => {
        setPage(read)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        const failed = asApiError(error)
        setPhase('broken')
        setFailure(failed.status === UNAUTHORIZED ? LOCKED_MESSAGE : failed.message)
      })

    return () => controller.abort()
  }, [reload])

  function done() {
    setEditing(null)
    setReload((count) => count + 1)
  }

  if (page !== null && editing !== null) {
    return (
      <LinkEditor
        link={editing.link}
        patterns={page.patterns}
        globals={page.globals}
        onSaved={done}
        onCancel={() => setEditing(null)}
      />
    )
  }

  const globals = page?.globals

  const columns: RecordColumn<AdminLink>[] = [
    {
      label: 'Link',
      kind: 'identity',
      cell: (link) => (
        <>
          <span className="cell-primary">{link.name}</span>
          <span className="cell-sub">/{link.slug}</span>
        </>
      ),
    },
    {
      label: 'Duration',
      kind: 'numeric',
      cell: (link) => `${link.durations.join(', ')} min`,
    },
    {
      label: 'Pattern',
      cell: (link) => <span className="badge">{link.patternName}</span>,
    },
    {
      label: 'Margin',
      kind: 'numeric',
      cell: (link) => (globals === undefined ? null : margin(link, globals)),
    },
    {
      label: 'Active',
      cell: (link) => (
        <span className="status" data-state={link.active ? LIVE : PAUSED}>
          {link.active ? 'Live' : 'Archived'}
        </span>
      ),
    },
    {
      label: 'Edit',
      kind: 'control',
      cell: (link) => (
        <button className="btn" type="button" onClick={() => setEditing({ link })}>
          Edit
        </button>
      ),
    },
  ]

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">Links</h1>
        <button
          className="btn btn-primary"
          type="button"
          disabled={page === null}
          onClick={() => setEditing({ link: null })}
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            aria-hidden="true"
          >
            <path d="M12 5v14M5 12h14" />
          </svg>
          New link
        </button>
      </div>

      {failure !== '' && (
        <p className="field-error mb-5" role="alert">
          <AlertIcon />
          {failure}
        </p>
      )}

      {/* `broken` draws no table at all: it has already put its one sentence
          above, and column headings over nothing would read as a list that came
          back empty rather than one that never came back. */}
      {phase === 'loading' ? (
        <p className="prose t-sm">Reading your links…</p>
      ) : page === null ? null : (
        <RecordTable
          columns={columns}
          rows={page.links}
          rowKey={(link) => link.id}
          empty={
            <div className="empty">
              <p>
                No links yet. A link is the address you send someone — it names the meeting, the
                lengths it runs for, and the hours it may be booked in.
              </p>
            </div>
          }
        />
      )}
    </>
  )
}
