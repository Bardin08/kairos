import { useRef, useState, type FormEvent } from 'react'

import {
  asApiError,
  createBooking,
  DATABASE_BUSY,
  INVITE_SPENT,
  type Booking,
  type Link,
} from '../lib/api'
import { endOf, longDate, zonedDate, zonedTime } from '../lib/time'
import AlertIcon from './AlertIcon'

const CONFLICT = 409

/** The server's own wait, stated in words. Never a backoff invented here. */
function wait(seconds: number | null): string {
  if (seconds === null) return 'in a moment'
  return seconds === 1 ? 'in a second' : `in ${seconds} seconds`
}

// Deliberately not an RFC 5322 implementation. The server validates properly
// and the mail either arrives or it does not; this exists to catch the typo the
// invitee can still fix, on the page, before they lose the slot to a round trip.
const LOOKS_LIKE_EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

type FieldErrors = { name?: string; email?: string }

/**
 * Who is booking, and the commit. Everything renders in the invitee's zone,
 * including the day: the organizer's calendar is off screen by now and has
 * nothing to anchor a `+1d` to.
 */
export default function BookingForm({
  link,
  start,
  durationMinutes,
  zone,
  details,
  inviteToken,
  onDetails,
  onBack,
  onBooked,
  onSlotLost,
}: {
  link: Link
  /** A verbatim instant from the slots read. */
  start: string
  durationMinutes: number
  /** The INVITEE's zone. Sent as `timeZone`, which is theirs, not the host's. */
  zone: string
  /**
   * Held by the caller rather than here, so a 409 can unmount this form and
   * send the invitee back to the times without throwing away what they typed.
   */
  details: { name: string; email: string; notes: string }
  /**
   * The invite this booking is spending, or null for an ordinary one. Only ever
   * a token the read said was open — the caller drops a stale one before it gets
   * here, so a refusal below is a race and not the ordinary state of a page.
   */
  inviteToken: string | null
  onDetails: (next: { name: string; email: string; notes: string }) => void
  onBack: () => void
  onBooked: (booking: Booking) => void
  /** The slot went while the form was open. Carries the server's account of it. */
  onSlotLost: (message: string) => void
}) {
  const { name, email, notes } = details
  const [errors, setErrors] = useState<FieldErrors>({})
  const [failure, setFailure] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  // Flipped by a refused invitation and never back. The failure that sets it
  // tells the invitee to send the booking again, and a second press carrying the
  // same token would be refused the same way — so the recovery has to be real.
  const inviteSpent = useRef(false)

  const day = zonedDate(start, zone)
  const span = `${zonedTime(start, zone)}–${zonedTime(endOf(start, durationMinutes), zone)}`

  async function submit(event: FormEvent) {
    event.preventDefault()
    // The loading state stops the pointer but not the Enter key, and a second
    // POST would be a second booking. The button is not disabled instead,
    // because DESIGN.md keeps the label in place under the spinner.
    if (sending) return

    const found: FieldErrors = {}
    if (name.trim() === '') {
      found.name = 'Your name goes on the invitation, so it cannot be blank.'
    }
    if (!LOOKS_LIKE_EMAIL.test(email.trim())) {
      found.email = 'The invitation is sent here, so it has to be an address that works.'
    }

    setErrors(found)
    setFailure(null)
    if (found.name !== undefined || found.email !== undefined) return

    setSending(true)
    try {
      onBooked(
        await createBooking({
          slug: link.slug,
          // Echoed back exactly as it arrived. Re-deriving it into an offset
          // form such as +02:00 is refused by the server.
          start,
          durationMinutes,
          name: name.trim(),
          email: email.trim(),
          timeZone: zone,
          notes: notes.trim(),
          // Omitted rather than sent as null: the server reads its absence as an
          // ordinary booking, and a token it has already refused would only be
          // refused again.
          inviteToken: inviteSpent.current ? undefined : (inviteToken ?? undefined),
        }),
      )
    } catch (error: unknown) {
      const problem = asApiError(error)
      // Before the status, because this is a 409 whose slot is still free.
      // Routing it through onSlotLost would take a time away from somebody who
      // can still have it — the same reasoning DATABASE_BUSY states below, for
      // the same reason: nothing was written.
      if (problem.code === INVITE_SPENT) {
        inviteSpent.current = true
        setFailure(
          'That personal invitation cannot be used any more — it was used, withdrawn, or its deadline passed. This time is still free and everything you typed is still here: send it again to book it without the invitation.',
        )
        return
      }
      if (problem.status === CONFLICT) {
        // Somebody else got it, or the calendar moved underneath. The slot has
        // to disappear from the list before the invitee is told, which is the
        // caller's job — this form has no list.
        onSlotLost(problem.message)
        return
      }
      if (problem.code === DATABASE_BUSY) {
        // A lost lock is not a lost slot. Nothing was written, the time is
        // still there, and the same button sends the same booking again.
        // Routing this through onSlotLost would take a time away from someone
        // who can still have it.
        setFailure(
          `The server was busy, so nothing was booked. Everything you typed is still here — send it again ${wait(problem.retryAfter)}.`,
        )
        return
      }
      setFailure(problem.message)
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="card card-lg card-pad card-narrow">
      <button className="btn btn-ghost btn-inline mb-5" type="button" onClick={onBack}>
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.8"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
        >
          <path d="M14.5 5.5L8 12l6.5 6.5" />
        </svg>
        Change time
      </button>

      <h1 className="title-page mb-5">Your details</h1>

      {/* What is being committed, in full, above the control that commits it. */}
      <div className="summary mb-6">
        <p className="summary-when numeric">
          {longDate(day)}, {span}
        </p>
        <p className="summary-meta numeric">
          {durationMinutes} minutes · {zone} · {link.name}
        </p>
      </div>

      <form className="stack stack-lg" onSubmit={submit} noValidate>
        <div className="field" data-invalid={errors.name === undefined ? undefined : 'true'}>
          <label htmlFor="booking-name">Your name</label>
          <input
            className="input"
            id="booking-name"
            name="name"
            autoComplete="name"
            value={name}
            onChange={(event) => onDetails({ ...details, name: event.target.value })}
            aria-invalid={errors.name === undefined ? undefined : true}
            aria-describedby={errors.name === undefined ? undefined : 'booking-name-error'}
          />
          {errors.name !== undefined && (
            <p className="field-error" id="booking-name-error">
              <AlertIcon />
              {errors.name}
            </p>
          )}
        </div>

        <div className="field" data-invalid={errors.email === undefined ? undefined : 'true'}>
          <label htmlFor="booking-email">Email</label>
          <input
            className="input"
            id="booking-email"
            name="email"
            type="email"
            autoComplete="email"
            value={email}
            onChange={(event) => onDetails({ ...details, email: event.target.value })}
            aria-invalid={errors.email === undefined ? undefined : true}
            aria-describedby={errors.email === undefined ? undefined : 'booking-email-error'}
          />
          {errors.email !== undefined && (
            <p className="field-error" id="booking-email-error">
              <AlertIcon />
              {errors.email}
            </p>
          )}
        </div>

        <div className="field">
          <label htmlFor="booking-notes">Anything to add?</label>
          <textarea
            className="textarea"
            id="booking-notes"
            name="notes"
            placeholder="One concrete question."
            value={notes}
            onChange={(event) => onDetails({ ...details, notes: event.target.value })}
          />
        </div>

        <button
          className="btn btn-primary btn-block"
          type="submit"
          data-loading={sending ? 'true' : undefined}
        >
          Confirm booking
        </button>

        {/* Under the control that failed, never a banner above the form. */}
        {failure !== null && (
          <p className="field-error" role="alert">
            <AlertIcon />
            {failure}
          </p>
        )}

        <p className="fine">
          The next screen carries the only link that can cancel or move this booking. It is shown once.
        </p>
      </form>
    </div>
  )
}
