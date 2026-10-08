import type { ReactNode } from 'react'

import BrandMark from '../../components/BrandMark'
import LegalFooter from '../../components/LegalFooter'
import { LANDING_PATH } from '../../lib/legal'

export default function LegalLayout({
  title,
  updated,
  children,
}: {
  title: string
  /** Changed whenever the text changes, and never otherwise. */
  updated?: string
  children: ReactNode
}) {
  return (
    <div className="page">
      <header className="legal-head">
        <a className="wordmark" href={LANDING_PATH}>
          <BrandMark />
          Kairos
        </a>
      </header>
      <main className="card card-lg card-pad legal">
        <h1 className="title-page">{title}</h1>
        {updated && <p className="fine">Last updated {updated}</p>}
        {children}
      </main>
      <LegalFooter attribution={false} />
    </div>
  )
}
