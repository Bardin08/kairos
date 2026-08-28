import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'wouter'

import Failure from './Failure'
import PatternPreview from './PatternPreview'
import { listPatterns, renamePattern, type Pattern } from './patterns'
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
 * It reads the whole list and picks its row out: there is no single-pattern GET,
 * and adding one would be a second endpoint to keep in step. The read goes
 * through patterns.ts, so a refused request latches session.ts and the shell
 * replaces itself with the locked screen.
 */
export default function PatternEditor({ params }: { params: { patternId: string } }) {
  const [pattern, setPattern] = useState<Pattern | null>(null)
  const [loading, setLoading] = useState(true)
  // The error itself, not a sentence made from it: `Failure` reads the status
  // off it, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  // Whether the name is being edited, and the text in the field. The name is
  // edited HERE rather than on the list: this page is where a pattern is worked
  // on, and its title is the name being read at the moment somebody decides it
  // is wrong.
  const [renaming, setRenaming] = useState(false)
  const [draftName, setDraftName] = useState('')
  const [savingName, setSavingName] = useState(false)

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

  /**
   * Enter and Save both arrive here — it is the form's submit, so Enter needs no
   * key handler of its own. An unchanged or blank name closes the editor without
   * calling the server: the endpoint refuses both, and a refusal would tell the
   * operator nothing the empty field has not already told them.
   */
  function commitRename(event: FormEvent) {
    event.preventDefault()
    if (pattern === null) return

    const trimmed = draftName.trim()
    if (trimmed === '' || trimmed === pattern.name) {
      setRenaming(false)
      return
    }

    setSavingName(true)
    setFailure(null)

    renamePattern(pattern.id, trimmed)
      .then((renamed) => {
        setPattern(renamed)
        setRenaming(false)
        setSavingName(false)
      })
      .catch((error: unknown) => {
        setSavingName(false)
        // Left open, with what was typed still in it. Closing the editor to show
        // a message about the name would throw away the name.
        setFailure(error)
      })
  }

  return (
    <>
      <div className="page-header">
        {renaming && pattern !== null ? (
          <form className="row gap-2 grow" onSubmit={commitRename}>
            <input
              className="input grow"
              aria-label="Pattern name"
              value={draftName}
              autoFocus
              onChange={(event) => setDraftName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Escape') setRenaming(false)
              }}
            />
            <button className="btn btn-primary" type="submit" data-loading={savingName || undefined}>
              Save
            </button>
            <button className="btn btn-ghost" type="button" onClick={() => setRenaming(false)}>
              Cancel
            </button>
          </form>
        ) : (
          <h1 className="title-page">
            {pattern === null ? (
              'Pattern'
            ) : (
              // A button inside the heading rather than a click handler on it: a
              // heading is not focusable, and a control the keyboard cannot reach
              // is not a control. `.name-button` strips the chrome so it still
              // reads as the title it stands in for.
              <button
                className="name-button"
                type="button"
                onClick={() => {
                  setRenaming(true)
                  setDraftName(pattern.name)
                  setFailure(null)
                }}
              >
                {pattern.name}
              </button>
            )}
          </h1>
        )}
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
          {/* No onChange of its own, deliberately: it writes overrides and the
              pane above is the only reader of the rest of the pattern. The
              reason that is not an oversight is in PatternPreview's header. */}
          <PatternPreview pattern={pattern} />
        </div>
      )}
    </>
  )
}
