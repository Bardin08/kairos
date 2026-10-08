import { TERMS_PATH } from '../../lib/legal'
import Contact from './Contact'
import LegalLayout from './LegalLayout'
import { capitalised, FALLBACK_OPERATOR, LIMITED_USE, SHORT_OPERATOR, useLegalDetails } from './legalDetails'

const UPDATED = '5 October 2026'

/**
 * Written from what the code does, not from what a booking tool usually does.
 * When behaviour changes — a new field stored, a purge job, another provider —
 * this text and {@link UPDATED} change in the same pull request.
 */
export default function Privacy() {
  const { operatorName, contactEmail } = useLegalDetails()
  const first = operatorName ?? FALLBACK_OPERATOR
  const later = operatorName ?? SHORT_OPERATOR
  const reach = contactEmail ? <Contact email={contactEmail} /> : later

  return (
    <LegalLayout title="Privacy policy" updated={UPDATED}>
      <section>
        <h2 className="title-section">Who is responsible</h2>
        <p>This booking service is run by {first}, who decides how the information you give it is used. In data-protection terms, {later} is the controller of your personal data.{contactEmail && (<>{' '}You can reach them at <Contact email={contactEmail} />.</>)}</p>
        <p>The service runs on Kairos, open-source software that {later} hosts on a server of their own. The people who make Kairos receive none of your data.</p>
      </section>

      <section>
        <h2 className="title-section">What is collected when you book</h2>
        <ul>
          <li>Your name and email address.</li>
          <li>A note, if you write one.</li>
          <li>If {later} sent you a personal booking link, the name, email address and note they filled in for you, held with that link before you book.</li>
          <li>Your time zone, read from your browser so times are shown in your own day.</li>
          <li>The time you choose, and later any reschedule or cancellation, with the reason if {later} cancels.</li>
        </ul>
        <p>Your IP address is held in memory for a few minutes to limit abuse of the booking form and is never written down. There is no analytics, no advertising and no tracking, and the pages load no scripts, fonts or images from anyone else.</p>
      </section>

      <section>
        <h2 className="title-section">Why it is used</h2>
        <p>To arrange the meeting you asked for: to hold the time, to send you a confirmation, a reminder and word of any change, and to put the meeting in {later}'s calendar. The legal basis is that you asked for it (Article 6(1)(b) GDPR). Limiting abuse of the form rests on {later}'s legitimate interest in keeping the service working (Article 6(1)(f) GDPR).</p>
      </section>

      <section>
        <h2 className="title-section">Who else receives it</h2>
        <ul>
          <li><strong>Google</strong>, when {later} has connected a Google Calendar. The meeting is added to that calendar as an event carrying your name, email address and note, with you as a guest, and a Google Meet link may be created for it. Google handles this under its own privacy policy.</li>
          <li><strong>The email provider</strong> {later} uses to deliver the service's emails, which handles your address and the message.</li>
          <li><strong>Another system</strong> {later} may connect to be told about bookings. It receives the booking's identifier, the link it was made through, and the meeting's time and length, never your name, email address or note.</li>
        </ul>
        <p>Your information is never sold, and never used for advertising.</p>
      </section>

      <section>
        <h2 className="title-section">Google user data</h2>
        <p>This part covers {later}'s own Google account, the one connected in the admin area so the service can check availability and add meetings. Invitees never sign in with Google.</p>
        <ul>
          <li><strong>Access requested:</strong> Google Calendar (<code>https://www.googleapis.com/auth/calendar</code>).</li>
          <li><strong>Read:</strong> the busy and free times of the calendars {later} selects, and the list of calendars with their names. Never the titles, descriptions or guests of existing events.</li>
          <li><strong>Written:</strong> only the events for bookings made through this service. They are updated when a booking is rescheduled and deleted when it is cancelled.</li>
          <li><strong>Stored:</strong> the refresh token Google issues, encrypted with AES-256-GCM, and the connected account's address, to label the connection. Busy times are used per request and not kept.</li>
          <li><strong>Shared:</strong> with nobody. Google user data is not sold, not used for advertising, not used to train AI models, and not read by people except to fix a problem {later} asks to have fixed.</li>
          <li><strong>Revoked:</strong> by disconnecting in the admin area, or at{' '}<a href="https://myaccount.google.com/permissions">myaccount.google.com/permissions</a>.</li>
        </ul>
        <p>{LIMITED_USE}</p>
      </section>

      <section>
        <h2 className="title-section">How long it is kept</h2>
        <p>Bookings, cancelled ones included, stay in the service's database until {later} removes them. The service deletes nothing on a schedule and has no delete button: a deletion request is carried out by {later} by hand, in the database. Ask {reach} to have yours removed.</p>
      </section>

      <section>
        <h2 className="title-section">Where it is kept</h2>
        <p>In one database on the server {later} runs. The links that let you manage a booking are stored as one-way hashes, or encrypted until the email carrying them has been sent, and calendar credentials are encrypted.</p>
      </section>

      <section>
        <h2 className="title-section">Cookies and local storage</h2>
        <p>Booking sets no cookie and stores nothing on your device. The admin area sets a session cookie when {later} signs in, strictly necessary and lasting up to 14 days, plus two short-lived cookies that protect the sign-in itself. In the admin area, {later}'s browser also remembers two settings in local storage: the light or dark theme (<code>theme</code>) and which notices were dismissed (<code>kairos.conditions.dismissed</code>). Neither leaves that device.</p>
      </section>

      <section>
        <h2 className="title-section">Your rights</h2>
        <p>You can ask for a copy of your data, have it corrected or deleted, restrict or object to its use, and receive it in a portable form. Write to {reach}. You can also complain to the data protection authority where you live or work.</p>
      </section>

      <section>
        <h2 className="title-section">Changes</h2>
        <p>{capitalised(later)} may update this policy; the date at the top changes when it does. The rules for using the service are in the <a href={TERMS_PATH}>terms of service</a>.</p>
      </section>
    </LegalLayout>
  )
}
