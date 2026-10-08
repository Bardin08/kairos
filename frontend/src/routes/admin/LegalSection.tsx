import { useId } from 'react'

import { LANDING_PATH, PRIVACY_PATH, TERMS_PATH } from '../../lib/legal'
import type { LegalDraft } from './adminSettings'


const FIELDS: { key: keyof LegalDraft; label: string; type: string; help: string }[] = [
  {
    key: 'operatorName',
    label: 'Your name',
    type: 'text',
    help: 'Or your company’s. Named as the person running this booking service.',
  },
  {
    key: 'contactEmail',
    label: 'Contact email',
    type: 'email',
    help: 'Where invitees send privacy requests. Shown as a link on the public pages.',
  },
  {
    key: 'jurisdiction',
    label: 'Governing law',
    type: 'text',
    help: 'The country whose law governs the terms, e.g. Poland.',
  },
]

const PAGES = [
  { label: 'Privacy policy', href: PRIVACY_PATH },
  { label: 'Terms of service', href: TERMS_PATH },
  { label: 'Landing page', href: LANDING_PATH },
]

/**
 * Who runs this instance, for the three public pages Kairos ships. Saved with
 * the rest of the screen by its one Save button, so it takes values and a
 * change handler rather than owning a request of its own.
 */
export default function LegalSection({
  draft,
  onChange,
}: {
  draft: LegalDraft
  onChange: (key: keyof LegalDraft, value: string) => void
}) {
  const id = useId()

  return (
    <div className="card card-pad">
      <p className="prose t-sm mb-4">Kairos serves a landing page, a privacy policy and terms of service on this instance — the three pages Google asks for before a calendar connection can leave testing. The text describes what Kairos does; these fill in who you are. Anything left empty reads as “the operator”.</p>
      <div className="stack">
        {FIELDS.map((field) => (
          <div className="field" key={field.key}>
            <label htmlFor={`${id}-${field.key}`}>{field.label}</label>
            <input
              className="input ctl-lg"
              id={`${id}-${field.key}`}
              type={field.type}
              value={draft[field.key]}
              onChange={(event) => onChange(field.key, event.target.value)}
            />
            <p className="fine">{field.help}</p>
          </div>
        ))}
      </div>
      {draft.contactEmail.trim() === '' && (
        <p className="fine mt-3">Without a contact email the privacy policy names no one to write to, and Google's consent-screen review will refuse it.</p>
      )}
      {/* Plain anchors: these are public pages outside the admin's `nest`, and
          a new tab keeps an unsaved draft on this screen. */}
      <div className="row wrap gap-2 mt-4">
        {PAGES.map((page) => (
          <a className="btn" href={page.href} target="_blank" rel="noopener" key={page.href}>
            {page.label} ↗
          </a>
        ))}
      </div>
    </div>
  )
}
