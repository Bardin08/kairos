import { useState, type FormEvent } from 'react'

import { asApiError, createBooking, type Booking, type Link } from '../lib/api'
import { endOf, longDate, zonedDate, zonedTime } from '../lib/time'

const CONFLICT = 409

// Deliberately not an RFC 5322 implementation. The server validates properly
// and the mail either arrives or it does not; this exists to catch the typo the
// invitee can still fix, on the page, before they lose the slot to a round trip.
const LOOKS_LIKE_EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

type FieldErrors = { name?: string; email?: string }

function AlertIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7.5v5M12 16h.01" />
    </svg>
  )
}

/**
 * Who is booking, and the commit.
 *
 * Everything here renders in the invitee's zone, including the day: by this
 * point the organizer's calendar is not on screen and has nothing to anchor a
 * `+1d` to, so the summary states the meeting as the invitee will live it.
 */
export default function BookingForm({
  link,
  start,
  durationMinutes,
  zone,
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
  onBack: () => void
  onBooked: (booking: Booking) => void
  /** The slot went while the form was open. Carries the server's account of it. */
  onSlotLost: (message: string) => void
}) {
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [notes, setNotes] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [failure, setFailure] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

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
        }),
      )
    } catch (error: unknown) {
      const problem = asApiError(error)
      if (problem.status === CONFLICT) {
        // Somebody else got it, or the calendar moved underneath. The slot has
        // to disappear from the list before the invitee is told, which is the
        // caller's job — this form has no list.
        onSlotLost(problem.message)
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
            onChange={(event) => setName(event.target.value)}
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
            onChange={(event) => setEmail(event.target.value)}
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
            onChange={(event) => setNotes(event.target.value)}
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
          The next screen carries the only link that can cancel or move this booking. It is shown
          once.
        </p>
      </form>
    </div>
  )
}
