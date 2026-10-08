import { PRIVACY_PATH } from '../../lib/legal'
import Contact from './Contact'
import LegalLayout from './LegalLayout'
import {
  capitalised,
  FALLBACK_JURISDICTION,
  FALLBACK_OPERATOR,
  SHORT_OPERATOR,
  useLegalDetails,
} from './legalDetails'

const UPDATED = '5 October 2026'

export default function Terms() {
  const { operatorName, contactEmail, jurisdiction } = useLegalDetails()
  const first = operatorName ?? FALLBACK_OPERATOR
  const later = operatorName ?? SHORT_OPERATOR

  return (
    <LegalLayout title="Terms of service" updated={UPDATED}>
      <section>
        <h2 className="title-section">The service</h2>
        <p>This booking service is offered by {first} so that people can schedule meetings with them. These terms apply whenever you use it, and booking a time means you accept them.</p>
      </section>

      <section>
        <h2 className="title-section">Booking</h2>
        <p>A booking is made when the page confirms it and the confirmation email arrives. Use the link in that email to reschedule or cancel. {capitalised(later)} may suggest another time or cancel a booking, and you will be told by email when that happens.</p>
      </section>

      <section>
        <h2 className="title-section">Acceptable use</h2>
        <ul>
          <li>Book with your own name and email address, and only for meetings you intend to attend.</li>
          <li>Do not book automatically or in bulk, and do not try to overload, probe or get around the service's limits.</li>
          <li>Do not use the note field for spam, or for anything unlawful or abusive.</li>
        </ul>
        <p>{capitalised(later)} may cancel bookings and refuse use that breaks these rules.</p>
      </section>

      <section>
        <h2 className="title-section">No warranty</h2>
        <p>The service is provided as is and as available. It may be unavailable or contain errors, and a booking may fail to reach a calendar. Confirm anything important with {later} directly.</p>
      </section>

      <section>
        <h2 className="title-section">Liability</h2>
        <p>As far as the law allows, {later} is not liable for indirect or consequential loss arising from the service, including a missed or double-booked meeting. Nothing here limits liability that the law does not allow to be limited.</p>
      </section>

      <section>
        <h2 className="title-section">Your information</h2>
        <p>What is collected and why is in the <a href={PRIVACY_PATH}>privacy policy</a>.</p>
      </section>

      <section>
        <h2 className="title-section">The software</h2>
        <p>The service runs on Kairos, open-source software licensed under the GNU Affero General Public License v3.0. These terms cover this service, not the software's licence.</p>
      </section>

      <section>
        <h2 className="title-section">Governing law</h2>
        <p>These terms are governed by the laws of {jurisdiction ?? FALLBACK_JURISDICTION}. Mandatory consumer protections of the country where you live still apply.</p>
      </section>

      <section>
        <h2 className="title-section">Changes and contact</h2>
        <p>{capitalised(later)} may update these terms; the date at the top changes when they do.{contactEmail && (<>{' '}Questions go to <Contact email={contactEmail} />.</>)}</p>
      </section>
    </LegalLayout>
  )
}
