import { useEffect, useRef, useState } from 'react'

import RecordTable, { type RecordColumn } from '../../components/RecordTable'
import { localZone, zonedDate, zonedTime } from '../../lib/time'
import {
  createInvite,
  listInvites,
  regenerateInvite,
  revokeInvite,
  type AdminInvite,
  type AdminInviteState,
  type IssuedInvite,
} from './adminInvites'
import Failure from './Failure'

// The two status shapes kairos.css draws, and the Second Channel Rule is why
// they are shapes: `live` fills the 7px dot, `paused` leaves it hollow at the
// same size, so the state survives a monochrome screenshot. Which of the three
// closed states a row is in is carried by the word beside the dot, never by the
// dot alone — three hues would be three states told in colour only.
const LIVE = 'live'
const PAUSED = 'paused'

/** The one state an invite can be in and still fill a booking form in. */
const OPEN: AdminInviteState = 'open'

const HOURS_PER_DAY = 24
const DAYS_PER_WEEK = 7
const MS_PER_HOUR = 3_600_000

/** The select's value for an invite that never lapses. */
const NO_DEADLINE = ''

/**
 * The deadlines on offer, held as durations and resolved to an instant at the
 * moment Generate is pressed — the wire carries a moment and never a duration,
 * and a preset resolved any earlier would be measured from the wrong clock read.
 */
const DEADLINES = [
  { hours: NO_DEADLINE, label: 'No deadline' },
  { hours: String(HOURS_PER_DAY), label: 'In 24 hours' },
  { hours: String(HOURS_PER_DAY * DAYS_PER_WEEK), label: 'In 7 days' },
]

/**
 * How a state reads to the operator, which is not always how it reads on the
 * wire: `consumed` is what the backend calls a spent invite, and "Used" is what
 * somebody looking at a list of invitations is asking about it.
 */
const STATE_WORDS: Record<AdminInviteState, string> = {
  open: 'Open',
  consumed: 'Used',
  revoked: 'Revoked',
  expired: 'Expired',
}

/** What a row says when the invite names nobody. Not a blank cell — see `addressee`. */
const UNADDRESSED_TITLE = 'Nobody in particular'
const UNADDRESSED_DETAIL = 'A working single-use link that fills nothing in.'

type Phase = 'loading' | 'ready' | 'broken'

function deadlineAt(hours: string): string | null {
  if (hours === NO_DEADLINE) return null
  return new Date(Date.now() + Number(hours) * MS_PER_HOUR).toISOString()
}

/**
 * Who an invite is for, and what to say when it is for nobody in particular.
 *
 * An empty prefill is not a broken row: it is a working single-use URL that
 * simply seeds nothing, and a blank cell is the one rendering of that fact which
 * reads as missing data instead.
 */
function addressee(invite: AdminInvite) {
  const name = invite.prefill.name ?? ''
  const email = invite.prefill.email ?? ''

  if (name === '' && email === '') {
    return (
      <>
        <span className="cell-primary">{UNADDRESSED_TITLE}</span>
        <span className="cell-sub">{UNADDRESSED_DETAIL}</span>
      </>
    )
  }

  return (
    <>
      <span className="cell-primary">{name === '' ? email : name}</span>
      {name !== '' && email !== '' && <span className="cell-sub">{email}</span>}
    </>
  )
}

/**
 * The operator's personal links for one link: issue one, see where each stands,
 * take one back, replace one whose URL was lost.
 *
 * Mounted only for a link that exists — an unsaved link has no id, and every
 * route here hangs off one.
 */
export default function InviteList({ linkId }: { linkId: string }) {
  const [invites, setInvites] = useState<AdminInvite[]>([])
  const [phase, setPhase] = useState<Phase>('loading')
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  const [issued, setIssued] = useState<IssuedInvite | null>(null)
  const [copied, setCopied] = useState(false)
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [hours, setHours] = useState(NO_DEADLINE)
  const [busy, setBusy] = useState(false)
  // Bumped after every write. The list re-reads rather than patching its own
  // copy, because the server decides the order and folds the clock into each
  // state — and a local patch is where those two drift apart.
  const [reload, setReload] = useState(0)
  const urlField = useRef<HTMLInputElement>(null)

  const zone = localZone()

  useEffect(() => {
    const controller = new AbortController()

    listInvites(linkId, controller.signal)
      .then((read) => {
        setInvites(read)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setPhase('broken')
        setFailure(error)
      })

    return () => controller.abort()
  }, [linkId, reload])

  // The announcement a server-rendered page would have got for free. A URL that
  // exists once has to reach the operator whether or not they are looking at
  // this corner of the editor, and focusing the field reads its label and its
  // value out — where a live region mounted with its text already in it is not
  // reliably announced at all. `onFocus` selects, so the next keystroke can be
  // the copy.
  useEffect(() => {
    if (issued !== null) urlField.current?.focus()
  }, [issued])

  /**
   * One write, and the refresh behind it. The URL is put on screen before the
   * refresh is asked for, deliberately: a read that fails afterwards must not
   * take the one copy of a URL down with it.
   */
  async function write(work: () => Promise<IssuedInvite | null>) {
    setBusy(true)
    setFailure(null)

    try {
      const minted = await work()
      if (minted !== null) {
        setIssued(minted)
        setCopied(false)
      }
      setReload((count) => count + 1)
    } catch (error: unknown) {
      setFailure(error)
    } finally {
      setBusy(false)
    }
  }

  function generate() {
    return write(async () => {
      const prefill: Record<string, string> = {}
      if (name.trim() !== '') prefill.name = name.trim()
      if (email.trim() !== '') prefill.email = email.trim()

      const minted = await createInvite(linkId, { prefill, expiresAt: deadlineAt(hours) })
      setName('')
      setEmail('')
      return minted
    })
  }

  function revoke(invite: AdminInvite) {
    return write(async () => {
      await revokeInvite(linkId, invite.id)
      // The field above is showing this invite's own URL, and that URL no longer
      // works. Another invite's stays: an operator who has not copied it yet
      // would lose it for good.
      if (issued?.id === invite.id) setIssued(null)
      return null
    })
  }

  function regenerate(invite: AdminInvite) {
    return write(() => regenerateInvite(linkId, invite.id))
  }

  const columns: RecordColumn<AdminInvite>[] = [
    {
      label: 'For',
      kind: 'identity',
      cell: addressee,
    },
    {
      label: 'Deadline',
      kind: 'numeric',
      cell: (invite) =>
        invite.expiresAt === null ? (
          <span className="muted t-xs">No deadline</span>
        ) : (
          `${zonedDate(invite.expiresAt, zone)} ${zonedTime(invite.expiresAt, zone)}`
        ),
    },
    {
      label: 'State',
      cell: (invite) => (
        <span className="status" data-state={invite.state === OPEN ? LIVE : PAUSED}>
          {STATE_WORDS[invite.state]}
        </span>
      ),
    },
    {
      label: 'Actions',
      kind: 'control',
      // Drawn on an open invite only, and the two halves have separate reasons.
      // Regenerate is refused 409 `invite_not_open` on the other three states.
      // Revoke is idempotent, so on those three it answers 204 having written
      // nothing. A button that always fails and a button that never does
      // anything are both worse than no button.
      cell: (invite) =>
        invite.state !== OPEN ? null : (
          <div className="row gap-2 wrap">
            <button
              className="btn"
              type="button"
              disabled={busy}
              onClick={() => void regenerate(invite)}
            >
              Regenerate
            </button>
            <button
              className="btn btn-ghost btn-danger"
              type="button"
              disabled={busy}
              onClick={() => void revoke(invite)}
            >
              Revoke
            </button>
          </div>
        ),
    },
  ]

  return (
    <section className="card card-pad mt-4 stack">
      <h2 className="title-section">Personal links</h2>

      <p className="prose t-sm">
        A personal link is this link with an invitation on it: the same hours, the same lengths,
        and a booking form that arrives filled in. It books once and is then spent.
      </p>

      <div className="field">
        <label htmlFor="invite-name">Their name</label>
        <input
          id="invite-name"
          className="input"
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </div>

      <div className="field">
        <label htmlFor="invite-email">Their email</label>
        <input
          id="invite-email"
          className="input"
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
        />
      </div>

      <div className="field">
        <label htmlFor="invite-deadline">Deadline</label>
        {/* The zone is said here rather than over the column below: it is the
            same sentence for both, and this is where an operator decides. */}
        <p className="fine">
          After it, the URL stops working and the times go back on offer. Deadlines are set and
          shown in your own zone, {zone}.
        </p>
        <select
          id="invite-deadline"
          className="select ctl-md"
          value={hours}
          onChange={(event) => setHours(event.target.value)}
        >
          {DEADLINES.map((deadline) => (
            <option key={deadline.label} value={deadline.hours}>
              {deadline.label}
            </option>
          ))}
        </select>
      </div>

      {/* Not `btn-primary`: this view's primary action is Save link in the
          savebar below, and DESIGN.md spends the accent on one action per view. */}
      <button className="btn" type="button" disabled={busy} onClick={() => void generate()}>
        Generate link
      </button>

      <Failure error={failure} />

      {/* The 201 is the only response that ever carries a URL — the server keeps
          a SHA-256 of the token and can no more read one back than the operator
          can. Same shape the booking page gives the manage link, for the same
          reason. */}
      {issued !== null && (
        <div className="field">
          <div className="row row-between">
            <label htmlFor="invite-url">The link to send</label>
            <button
              className="btn btn-ghost"
              type="button"
              aria-label="Copy personal link"
              onClick={() => {
                void navigator.clipboard.writeText(issued.url)
                setCopied(true)
              }}
            >
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
          <p className="fine">
            Copy it now. It is shown once and cannot be read back — if it is lost, regenerate the
            invite for a new one.
          </p>
          <input
            id="invite-url"
            className="input"
            ref={urlField}
            readOnly
            value={issued.url}
            onFocus={(event) => event.currentTarget.select()}
          />
        </div>
      )}

      {/* `broken` draws no table at all: `Failure` has already put its one
          sentence above, and column headings over nothing would read as a list
          that came back empty rather than one that never came back. */}
      {phase === 'loading' && <p className="prose t-sm">Reading the personal links…</p>}

      {phase === 'ready' && (
        <RecordTable
          columns={columns}
          rows={invites}
          rowKey={(invite) => invite.id}
          empty={
            <div className="empty">
              <p>
                No personal links yet. Generate one when you want to invite a particular person —
                the ordinary link above stays open to anyone who has it.
              </p>
            </div>
          }
        />
      )}
    </section>
  )
}
