import { PRIVACY_PATH, TERMS_PATH } from '../../lib/legal'
import Contact from './Contact'
import LegalLayout from './LegalLayout'
import { capitalised, FALLBACK_OPERATOR, SHORT_OPERATOR, useLegalDetails } from './legalDetails'

/**
 * What someone sees on the bare domain: an invitee who trimmed their link, or
 * Google's reviewer checking the home page named on the consent screen. No
 * sign-in button and no list of links — the operator's links are handed out,
 * not published.
 */
export default function Landing() {
  const { operatorName, contactEmail } = useLegalDetails()
  const first = operatorName ?? FALLBACK_OPERATOR
  const later = operatorName ?? SHORT_OPERATOR

  return (
    <LegalLayout title={operatorName ? `Book a time with ${operatorName}` : 'Book a time'}>
      <p>This is a booking service run by {first}. It lets people pick a time to meet from {later}'s real availability, without emailing back and forth.</p>
      <p>A booking link looks like <code>{window.location.origin}/book/&lt;name&gt;</code>.{' '}{capitalised(later)} shares those links directly; there is no public list of them. If you have already booked, the confirmation email holds a link to see, reschedule or cancel your meeting.</p>

      <section>
        <h2 className="title-section">Your information</h2>
        <p>Booking asks for your name, your email address and, if you like, a note. How that is used is in the <a href={PRIVACY_PATH}>privacy policy</a>; the rules for using this service are in the <a href={TERMS_PATH}>terms of service</a>.</p>
        {contactEmail && (
          <p>Questions go to <Contact email={contactEmail} />.</p>
        )}
      </section>

      <p className="fine">This service runs on Kairos, open-source scheduling software.</p>
    </LegalLayout>
  )
}
