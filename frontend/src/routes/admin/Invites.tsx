import { useEffect, useRef, useState } from 'react'

import RecordTable, { type RecordColumn } from '../../components/RecordTable'
import { localZone, zonedDate, zonedTime } from '../../lib/time'
import {
  createInvite,
  listAllInvites,
  regenerateInvite,
  revokeInvite,
  type AdminInvite,
  type AdminInviteState,
  type IssuedInvite,
} from './adminInvites'
import { listLinks, type AdminLink } from './adminLinks'
import Failure from './Failure'
import SectionNav from './SectionNav'

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

/** No link picked yet, which is the state until the links have been read. */
const NO_LINK = ''

/** What the header button controls, named so `aria-controls` can point at it. */
const PANEL_ID = 'invite-panel'

/** The pair of controls `Change` reveals, named so `aria-controls` can say so. */
const SETTINGS_ID = 'invite-settings'

/**
 * The deadlines on offer, held as durations and resolved to an instant at the
 * moment Generate is pressed — the wire carries a moment and never a duration,
 * and a preset resolved any earlier would be measured from the wrong clock read.
 *
 * Two words apiece, because the choice is read in two places: `label` starts an
 * <option> and is capitalised for it, `settled` sits mid-sentence after a middot.
 */
const DEADLINES = [
  { hours: NO_DEADLINE, label: 'No deadline', settled: 'no deadline' },
  { hours: String(HOURS_PER_DAY), label: 'In 24 hours', settled: 'good for 24 hours' },
  { hours: String(HOURS_PER_DAY * DAYS_PER_WEEK), label: 'In 7 days', settled: 'good for 7 days' },
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

/** Nothing has been issued yet, on any link. */
const NO_INVITES =
  'No personal links yet. Generate one when you want to invite a particular person — the ordinary link stays open to anyone who has it.'

/**
 * Invites exist, but none of them is still open. Names the recovery rather than
 * only the fact, because the rows the filter is hiding are still on the page.
 */
const NONE_OPEN =
  'No personal link is still open. All shows the ones already used, revoked or expired.'

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
 * The operator's personal links, across every link they have: issue one, see
 * where each stands, take one back, replace one whose URL was lost.
 *
 * A page rather than a section of the link editor. An invite is issued against
 * one link and its own actions are routed under that link, but the question the
 * operator brings — which of the URLs I have handed out is still live — is asked
 * about all of them at once, and a per-link list can only ever answer a slice.
 */
export default function Invites() {
  const [invites, setInvites] = useState<AdminInvite[]>([])
  // The links the picker may offer, null until they have been read — so "none
  // to offer" and "not read yet" stay distinguishable: one is the empty state
  // below and the other is a form still waiting for its options.
  const [offered, setOffered] = useState<AdminLink[] | null>(null)
  const [phase, setPhase] = useState<Phase>('loading')
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  const [issued, setIssued] = useState<IssuedInvite | null>(null)
  const [copied, setCopied] = useState(false)
  const [linkId, setLinkId] = useState(NO_LINK)
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [hours, setHours] = useState(NO_DEADLINE)
  // Open, not All. The page answers which of the URLs handed out are still
  // live; used, revoked and expired rows are never cleaned up and accumulate
  // permanently, so All is where the archive is and Open is the question.
  const [showAll, setShowAll] = useState(false)
  const [busy, setBusy] = useState(false)
  // Whether the panel is showing. Not derived from `issued`: the panel is open
  // before anything is issued, and stays open on the URL after. Regenerate sets
  // both, because a URL minted from a row has nowhere else to be shown.
  const [panelOpen, setPanelOpen] = useState(false)
  // Whether the link picker and the deadline are showing as controls rather than
  // as the sentence that states them.
  const [changing, setChanging] = useState(false)
  // Bumped after every write. The list re-reads rather than patching its own
  // copy, because the server decides the order and folds the clock into each
  // state — and a local patch is where those two drift apart.
  const [reload, setReload] = useState(0)
  const urlField = useRef<HTMLInputElement>(null)

  const zone = localZone()

  useEffect(() => {
    const controller = new AbortController()

    listAllInvites(controller.signal)
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
  }, [reload])

  // Read once and not on `reload`: nothing on this page creates or renames a
  // link, and re-reading them after every invite written would be a request
  // whose answer cannot have changed.
  useEffect(() => {
    const controller = new AbortController()

    listLinks(controller.signal)
      .then((page) => {
        // Live links only, and refused here rather than at the server. An
        // archived link's booking page answers 404 — LinksEndpoints filters on
        // `link.Active` — but nothing on the invite path checks: InvitesEndpoints
        // resolves the invite and hands back `invite.Link.Slug` regardless, so an
        // invite issued against an archived link mints a URL that is valid and
        // dead on arrival. A refusal code would cost an endpoint, its tests and a
        // branch here, all to stop something this picker no longer offers.
        const live = page.links.filter((link) => link.active)
        setOffered(live)
        // Seeded rather than left empty: `linkId` is what routes the POST, and
        // nothing else sets it until the operator touches the picker.
        if (live.length > 0) setLinkId(live[0].id)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setFailure(error)
      })

    return () => controller.abort()
  }, [])

  // The announcement a server-rendered page would have got for free. A URL that
  // exists once has to reach the operator whether or not they are looking at
  // this corner of the page, and focusing the field reads its label and its
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
        // Regenerate is pressed from a row and does not go through the panel, so
        // the panel has to come to the URL. Generate is already inside an open
        // panel and this is a no-op there.
        setPanelOpen(true)
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

  /**
   * Closing forgets the URL along with the form. It is not a loss the operator
   * cannot undo: regenerate revokes the invite and mints a replacement carrying
   * the same prefill and the same deadline, and it is one press on the row. A
   * panel that refused to close to protect a recoverable mistake would cost more
   * than the mistake does.
   */
  function closePanel() {
    setPanelOpen(false)
    setIssued(null)
    setCopied(false)
  }

  // Both of these take the invite's own link rather than the picker's: the
  // picker says what the next invite will be issued against, and a row on this
  // page need not be on that link at all.
  function revoke(invite: AdminInvite) {
    return write(async () => {
      await revokeInvite(invite.linkId, invite.id)
      // The field above is showing this invite's own URL, and that URL no longer
      // works. Another invite's stays: an operator who has not copied it yet
      // would lose it for good.
      if (issued?.id === invite.id) setIssued(null)
      return null
    })
  }

  function regenerate(invite: AdminInvite) {
    return write(() => regenerateInvite(invite.linkId, invite.id))
  }

  const columns: RecordColumn<AdminInvite>[] = [
    {
      label: 'For',
      kind: 'identity',
      cell: addressee,
    },
    {
      label: 'Link',
      cell: (invite) => <span className="muted t-sm">{invite.linkName}</span>,
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

  // Filtered here rather than by the server: the whole set is already in hand,
  // one request answered every link, and asking again for a subset of what is
  // in memory would be a round trip to compute a `filter`.
  const shown = showAll ? invites : invites.filter((invite) => invite.state === OPEN)

  const nothingToOffer = offered !== null && offered.length === 0

  // What the sentence says, read off the same two pieces of state the controls
  // write — so the settled line and the revealed controls can never disagree.
  const chosenLink = (offered ?? []).find((link) => link.id === linkId)
  const chosenDeadline = DEADLINES.find((deadline) => deadline.hours === hours)

  return (
    <>
      {/* The same grouping the links table uses, doing the same job: on Links the
          primary action creates a link, here it creates a personal link. */}
      <div className="page-header">
        <h1 className="title-page">Personal links</h1>
        <div className="row gap-2">
          <SectionNav />
          {/* Dead while the picker holds no link, which covers two states that
              look alike from here: the window before the links land, and a links
              read that failed outright. Opening a panel that can only POST to
              `/admin/links//invites` stacks a second, unrelated failure on the
              first. With every link archived the empty state below says so
              instead, and this button is dead for the same reason. */}
          <button
            className={panelOpen ? 'btn btn-ghost' : 'btn btn-primary'}
            type="button"
            aria-expanded={panelOpen}
            aria-controls={PANEL_ID}
            disabled={linkId === NO_LINK}
            onClick={() => (panelOpen ? closePanel() : setPanelOpen(true))}
          >
            {panelOpen ? (
              'Cancel'
            ) : (
              <>
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
                New personal link
              </>
            )}
          </button>
        </div>
      </div>

      {nothingToOffer ? (
        // No picker, because there is nothing to put in it. One sentence for
        // both ways of getting here — no links at all, and every link archived —
        // because it names both recoveries and the reader knows which is theirs.
        <div className="empty">
          <p>
            No live link to invite anyone to yet. A personal link is an invitation on one of your links, borrowing its hours and its lengths — so make one, or restore one you have archived.
          </p>
        </div>
      ) : (
        panelOpen && (
          <section id={PANEL_ID} className="card card-pad stack">
            {/* One thought, so one row: the operator reaching for this is thinking
                about a person, not about four settings. */}
            <div className="row gap-4 wrap">
              <div className="field grow">
                <label htmlFor="invite-name">Their name</label>
                <input
                  id="invite-name"
                  className="input"
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                />
              </div>

              <div className="field grow">
                <label htmlFor="invite-email">Their email</label>
                <input
                  id="invite-email"
                  className="input"
                  type="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                />
              </div>
            </div>

            {/* The two decisions that are usually already right, stated rather than
                asked. `.fact` and its `strong` are the stylesheet's existing "one
                phrase at full strength in a quiet line" — and the link name is the
                phrase that has to carry, because the picker is hidden and issuing
                against the wrong link is not visible until the recipient books the
                wrong meeting. */}
            <p className="fact">
              <span>
                On <strong>{chosenLink?.name ?? ''}</strong> · <strong>{chosenDeadline?.settled ?? ''}</strong>
              </span>
              <button
                className="btn btn-ghost"
                type="button"
                aria-expanded={changing}
                aria-controls={SETTINGS_ID}
                onClick={() => setChanging((shown) => !shown)}
              >
                Change
              </button>
            </p>

            {changing && (
              <div id={SETTINGS_ID} className="stack">
                <div className="field">
                  <label htmlFor="invite-link">Link</label>
                  <select
                    id="invite-link"
                    className="select ctl-md"
                    value={linkId}
                    onChange={(event) => setLinkId(event.target.value)}
                  >
                    {(offered ?? []).map((link) => (
                      <option key={link.id} value={link.id}>
                        {link.name}
                      </option>
                    ))}
                  </select>
                </div>

                <div className="field">
                  <label htmlFor="invite-deadline">Deadline</label>
                  {/* The zone is said here rather than over the column below: it is
                      the same sentence for both, and this is where an operator
                      decides. */}
                  <p className="fine">
                    After it, the URL stops working and the times go back on offer. Deadlines are set and shown in your own zone, {zone}.
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
              </div>
            )}

            {/* `btn-primary`, where the editor's copy of this form was a plain
                `btn`. DESIGN.md allows one primary per view, and in the editor
                that one was Save link in the savebar below. Here Generate is the
                only thing that commits, and the header button steps down to a
                ghost while the panel is open so it stays that way. */}
            {/* Dead until the picker holds a link, which covers two states that
                look alike: the window before the links land, and a links read that
                failed outright. In both `linkId` is still NO_LINK, and generating
                would POST to `/admin/links//invites` and stack a second, unrelated
                failure on the first. */}
            <button
              className="btn btn-primary"
              type="button"
              disabled={busy || linkId === NO_LINK}
              onClick={() => void generate()}
            >
              Generate link
            </button>

            {/* The 201 is the only response that ever carries a URL — the server
                keeps a SHA-256 of the token and can no more read one back than the
                operator can. Same shape the booking page gives the manage link,
                for the same reason. */}
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
                  Copy it now. It is shown once and cannot be read back — if it is lost, regenerate the invite for a new one.
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
          </section>
        )
      )}

      {/* Outside the card, where the editor's copy sat inside it under Generate.
          The card is a branch now — with no live link there is no form to sit
          under — and a failed links read has to be said on the very page that
          branch removes. */}
      <Failure error={failure} className="mt-4" />

      {/* `broken` draws no table at all: `Failure` has already put its one
          sentence above, and column headings over nothing would read as a list
          that came back empty rather than one that never came back. */}
      {phase === 'loading' && <p className="prose t-sm mt-4">Reading the personal links…</p>}

      {phase === 'ready' && !nothingToOffer && (
        <div className="mt-4">
          {/* Drawn only once there is something to filter — a control that can
              only ever answer "none either way" is furniture. `role="group"`
              with `aria-pressed`, which is DurationPicker's shape rather than
              PatternPreview's radiogroup: the same kairos.css rule paints both,
              and a radiogroup would owe a roving tabindex this does not need. */}
          {invites.length > 0 && (
            <div className="segmented mb-4" role="group" aria-label="Which invites">
              <button type="button" aria-pressed={!showAll} onClick={() => setShowAll(false)}>
                Open
              </button>
              <button type="button" aria-pressed={showAll} onClick={() => setShowAll(true)}>
                All
              </button>
            </div>
          )}

          <RecordTable
            columns={columns}
            rows={shown}
            rowKey={(invite) => invite.id}
            empty={
              <div className="empty">
                <p>{invites.length === 0 ? NO_INVITES : NONE_OPEN}</p>
              </div>
            }
          />
        </div>
      )}
    </>
  )
}
