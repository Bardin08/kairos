import { useEffect, useState } from 'react'
import { Link } from 'wouter'

import Failure from './Failure'
import { listPatterns, type Pattern } from './patterns'
import WeekEditor from './WeekEditor'

/**
 * Relative, because the shell is mounted with `nest` and wouter's base is
 * `/admin`. Writing the full path here would produce `/admin/admin/availability`.
 */
const LIST = '/availability'

/**
 * One pattern, on a page of its own: the week on the left, what it produces on
 * the right.
 *
 * It reads the whole list and picks its row out of it, and that is deliberate
 * rather than lazy — there is no single-pattern GET, and adding one would be a
 * second endpoint to keep in step with the list that already answers with
 * `rules`, `overrides` and `usedBy`. The read goes through patterns.ts, so a
 * refused request latches session.ts and the shell above replaces itself with
 * the locked screen; a fetch of its own here would put this page back to
 * drawing a heading and two empty panes at somebody who has no session.
 */
export default function PatternEditor({ params }: { params: { patternId: string } }) {
  const [pattern, setPattern] = useState<Pattern | null>(null)
  const [loading, setLoading] = useState(true)
  // The error itself, not a sentence made from it: `Failure` reads the status
  // off it, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)

  const { patternId } = params

  useEffect(() => {
    const controller = new AbortController()

    listPatterns(controller.signal)
      .then((list) => {
        setPattern(list.find((candidate) => candidate.id === patternId) ?? null)
        setLoading(false)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setLoading(false)
        setFailure(error)
      })

    return () => controller.abort()
  }, [patternId])

  // An id nobody's pattern has: a bookmark to one deleted in another tab, or a
  // hand-typed address. The page said what happened and points at the list —
  // there is nothing here for `Failure` to report, because nothing failed.
  const missing = !loading && failure === null && pattern === null

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">{pattern === null ? 'Pattern' : pattern.name}</h1>
        {/* A link and not a Back button: this is an address now, and the
            browser's own back button, a middle click and a bookmark all have to
            work. `.btn-ghost` is the retreat action's style. */}
        <Link className="btn btn-ghost" href={LIST}>
          Back to Availability
        </Link>
      </div>

      <Failure error={failure} className="mb-5" />

      {loading && <p className="prose t-sm">Reading this pattern…</p>}

      {missing && (
        <div className="empty">
          <p>
            This pattern is no longer there. It may have been deleted from another tab — the
            availability list has the ones that are left.
          </p>
        </div>
      )}

      {pattern !== null && (
        <div className="editor-cols">
          {/* The write answers with the whole pattern, so what comes back
              replaces this page's copy rather than being merged into it. */}
          <WeekEditor pattern={pattern} onChange={setPattern} />
          <section className="card card-pad">
            <h2 className="title-section">Preview</h2>
          </section>
        </div>
      )}
    </>
  )
}
