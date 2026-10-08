import { PRIVACY_PATH, TERMS_PATH } from '../lib/legal'

/**
 * Plain anchors rather than wouter's `Link`: under the admin's `nest` a `Link`
 * resolves against `/admin`, and a full load of a page of text costs nothing
 * worth saving.
 *
 * "Scheduled with Kairos" is the invitee's attribution line, so the operator's
 * surfaces and the legal pages themselves pass `attribution={false}`.
 */
export default function LegalFooter({ attribution = true }: { attribution?: boolean }) {
  return (
    <footer className="fine page-foot">
      {attribution && 'Scheduled with Kairos · '}
      <a href={PRIVACY_PATH}>Privacy</a> · <a href={TERMS_PATH}>Terms</a>
    </footer>
  )
}
