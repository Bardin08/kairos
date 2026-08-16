import { useEffect, useState } from 'react'

import RecordTable, { type RecordColumn } from '../../components/RecordTable'
import { localZone, todayIn, zonedDate, zonedTime } from '../../lib/time'
import { listMeetings, type Meeting, type Meetings as List } from './adminMeetings'
import Failure from './Failure'
import { shiftDays, shortMonth } from './adminTime'

// The two status shapes kairos.css draws, and the Second Channel Rule is the
// whole reason they are shapes rather than colours: `live` fills the 7px dot,
// `paused` leaves it hollow at the same size. Both states this page shows —
// confirmed against cancelled, synced against not synced — therefore survive a
// monochrome screenshot and a colour-blind reader, and the words beside the dot
// are what name them.
const LIVE = 'live'
const PAUSED = 'paused'

/** The one status a meeting can be in and still be happening. */
const CONFIRMED = 'confirmed'

/**
 * The three ranges the period filter offers.
 *
 * All three are date ranges, which is what the endpoint takes; the presets are
 * only the three an operator actually asks for. `RECENT` is bounded rather than
 * open because "everything behind me, oldest first" is a list that grows without
 * limit and answers nothing — and `ALL` is still there for the year no preset
 * covers.
 */
const UPCOMING = 'upcoming'
const RECENT = 'recent'
const ALL = 'all'

const RECENT_DAYS = 30

// Slices of a `YYYY-MM-DD` date key. Named rather than written as 0, 4 and 8 at
// the point of use, which is the difference between a slice and a magic number.
const YEAR_LENGTH = 'YYYY'.length
const DAY_OF_MONTH_AT = 'YYYY-MM-'.length

type Phase = 'loading' | 'ready' | 'broken'

/** The dates a period means, in the organizer's zone. Absent bound, absent key. */
function rangeOf(period: string, zone: string): { from?: string; to?: string } {
  const today = todayIn(zone)
  if (period === UPCOMING) return { from: today }
  if (period === RECENT) return { from: shiftDays(today, -RECENT_DAYS), to: today }
  return {}
}

/**
 * The date as the comp's record chip draws it — `docs/ui/admin.html` line 214 —
 * with the year as a third line inside it.
 *
 * The comp carries no year because it is showing four meetings in one month. A
 * list that spans "past and upcoming" spans years, and a bare "20 Aug" in it is
 * a date that does not say which one. It goes on its own quiet line rather than
 * beside the month, whose 54px chip would wrap the pair — and inside the chip
 * rather than under it, because a sibling of the chip is a full-width block that
 * takes the cell's own alignment, which is how the day, the month and the year
 * ended up on three different alignments.
 *
 * A `div` rather than the comp's `span`: `p` is not phrasing content, and the
 * chip is a block in every layout that uses it.
 */
function dateChip(instant: string, zone: string) {
  const day = zonedDate(instant, zone)

  return (
    <div className="record-date">
      <span className="record-day">{day.slice(DAY_OF_MONTH_AT)}</span>
      <span className="record-month">{shortMonth(day)}</span>
      <p className="record-meta">{day.slice(0, YEAR_LENGTH)}</p>
    </div>
  )
}

/**
 * What time the participant thought this was.
 *
 * The question that gets asked after a no-show, which is why the zone is stored
 * on the participant rather than derived from the organizer's. Null when the
 * booking carries no zone at all; the zone's own name when `Intl` does not carry
 * it, because the name still answers some of the question and a thrown
 * RangeError would take the whole page down over one row.
 */
function theirTime(meeting: Meeting): string | null {
  if (meeting.participantTimeZone === '') return null

  try {
    return `${zonedTime(meeting.start, meeting.participantTimeZone)} their time · ${meeting.participantTimeZone}`
  } catch {
    return meeting.participantTimeZone
  }
}

/** A provider's name as a heading, without this file knowing any provider. */
function label(provider: string): string {
  return provider.charAt(0).toUpperCase() + provider.slice(1)
}

/**
 * Why a meeting is not in the calendar, in the operator's terms.
 *
 * "Not synced" on its own sends them to the server log for something the row
 * already knows, so the provider's own words go on the detail. No error at all
 * means nothing was ever attempted, which is the ordinary state of a Kairos
 * whose operator never connected a calendar — not a fault, and it must not read
 * like one.
 */
function whyNotSynced(meeting: Meeting): string {
  if (meeting.calendarError === null) {
    return 'No calendar was connected when this was booked, so nothing was written.'
  }

  const provider = meeting.calendarProvider === null ? '' : `${label(meeting.calendarProvider)}: `
  return provider + meeting.calendarError
}

/**
 * Every meeting Kairos made, and the one fact a calendar cannot supply: the link
 * it came through.
 *
 * Read-only on purpose. Cancelling and moving already have a surface — Part A's
 * manage link — and a second one here would be two implementations of rules that
 * only hold while there is one. Meetings Kairos did not create are equally
 * absent: reading a calendar back is a slice with its own conflict rules.
 */
export default function Meetings() {
  const [period, setPeriod] = useState(UPCOMING)
  const [linkId, setLinkId] = useState('')
  const [list, setList] = useState<List | null>(null)
  const [phase, setPhase] = useState<Phase>('loading')
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)

  // Starts as the browser's and becomes the organizer's as soon as the first
  // answer says what it is. The zone decides what "today" means to the period
  // filter, and the filter has to be built before any answer exists — so the
  // browser's is the opening guess, the stored setting is the authority, and
  // where the two differ this effect runs once more with corrected dates and
  // settles. Nothing here hard-codes a zone, so Task 6's picker flows through.
  const [zone, setZone] = useState(localZone)

  useEffect(() => {
    const controller = new AbortController()
    const range = rangeOf(period, zone)

    listMeetings(
      { linkId: linkId === '' ? undefined : linkId, from: range.from, to: range.to },
      controller.signal,
    )
      .then((answer) => {
        setList(answer)
        setZone(answer.timeZone)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setFailure(error)
        setPhase('broken')
      })

    // Deliberately not resetting phase to `loading` here: the rows already on
    // screen stay until the narrower set replaces them, so changing a filter
    // does not blink the table out and back.
    return () => controller.abort()
  }, [period, linkId, zone])

  const columns: RecordColumn<Meeting>[] = [
    {
      label: 'Date',
      kind: 'numeric',
      cell: (meeting) => dateChip(meeting.start, zone),
    },
    {
      label: 'Time',
      kind: 'numeric',
      cell: (meeting) => (
        <time dateTime={meeting.start}>
          {zonedTime(meeting.start, zone)} – {zonedTime(meeting.end, zone)}
        </time>
      ),
    },
    {
      label: 'Duration',
      kind: 'numeric',
      cell: (meeting) => `${meeting.durationMinutes} min`,
    },
    {
      // The row's title, and the row's detail under it: who, how to reach them,
      // and what time they read this as. `docs/ui/admin.html` lines 216-218.
      label: 'Participant',
      kind: 'identity',
      cell: (meeting) => {
        const theirs = theirTime(meeting)

        return (
          <>
            <p className="record-who">{meeting.participantName}</p>
            <p className="record-meta">{meeting.participantEmail}</p>
            {theirs !== null && <p className="record-meta">{theirs}</p>}
          </>
        )
      },
    },
    {
      // Why this tab exists rather than deferring to the calendar.
      label: 'Link',
      cell: (meeting) => <span className="badge">{meeting.linkName}</span>,
    },
    {
      label: 'Status',
      cell: (meeting) => (
        <span className="status" data-state={meeting.status === CONFIRMED ? LIVE : PAUSED}>
          {meeting.status === CONFIRMED ? 'Confirmed' : 'Cancelled'}
        </span>
      ),
    },
    {
      label: 'Calendar',
      cell: (meeting) => (
        <div>
          <span className="status" data-state={meeting.calendarSynced ? LIVE : PAUSED}>
            {meeting.calendarSynced ? 'Synced' : 'Not synced'}
          </span>
          {!meeting.calendarSynced && <p className="record-meta">{whyNotSynced(meeting)}</p>}
        </div>
      ),
    },
  ]

  const filtered = linkId !== '' || period !== ALL

  return (
    <>
      {/* The comp's header, `docs/ui/admin.html` lines 200-210: the title and
          two inline selects. The second offers three ranges rather than the
          comp's Upcoming/Past/Cancelled, because status is a column on every row
          here and a filter for it is not something this task was asked for. */}
      <div className="page-header">
        <h1 className="title-page">Meetings</h1>
        <div className="row gap-2">
          <select
            className="select select-inline"
            aria-label="Filter by link"
            value={linkId}
            onChange={(event) => setLinkId(event.target.value)}
          >
            <option value="">All links</option>
            {list?.links.map((link) => (
              <option key={link.id} value={link.id}>
                {link.name}
              </option>
            ))}
          </select>
          <select
            className="select select-inline"
            aria-label="Filter by period"
            value={period}
            onChange={(event) => setPeriod(event.target.value)}
          >
            <option value={UPCOMING}>Upcoming</option>
            <option value={RECENT}>Last {RECENT_DAYS} days</option>
            <option value={ALL}>All</option>
          </select>
        </div>
      </div>

      {/* Said once, above the column of times it applies to. Every instant on
          this page is rendered in the organizer's stored zone, and a column of
          times belonging to nobody in particular is how a call gets missed. */}
      <p className="fine mb-5">Times are the organizer&apos;s, in {zone}.</p>

      {/* An icon as well as the red, the same way every other failure in this
          product is drawn, and an expired session gets a way back in rather
          than a status code. A table is rendered under it only if one arrived. */}
      {phase === 'broken' && <Failure error={failure} className="mb-5" />}

      {phase === 'loading' && <p className="prose t-sm">Reading the meetings…</p>}

      {phase === 'ready' && list !== null && (
        <RecordTable
          columns={columns}
          rows={list.meetings}
          rowKey={(meeting) => meeting.id}
          empty={
            <div className="empty">
              <p>
                {filtered
                  ? 'No meetings match this link and this period. Widen either one.'
                  : 'Nothing has been booked yet. Meetings appear here as soon as someone books one of your links.'}
              </p>
            </div>
          }
        />
      )}
    </>
  )
}
