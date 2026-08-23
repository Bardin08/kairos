import { useState } from 'react'

import DurationEditor from '../../components/DurationEditor'
import InheritableNumber from '../../components/InheritableNumber'
import {
  archiveLink,
  createLink,
  restoreLink,
  updateLink,
  updateLinkLimits,
  type AdminLink,
  type AdminPattern,
  type GlobalLimits,
  type LinkLimits,
} from './adminLinks'
import Failure from './Failure'
import { CLEAN, changedCount, draftOf, unsavedLabel, type EditorDraft } from './editorDraft'
import { LIMIT_COPY, LIMIT_KEYS } from './limitCopy'

/** Where a booking page lives. Fixed by App.tsx's route, not configurable. */
const BOOK_PATH = '/book/'

/**
 * One link, in full: what it is called, where it lives, how long it runs, which
 * hours it may be booked in, and which of the four global scheduling limits it
 * narrows.
 *
 * Three cards down a 620px column rather than one card across the page. The
 * previous version stretched every control to the width of a 1180px card, so a
 * two-digit minute count sat in a box a thousand pixels wide while the help
 * text beside it stopped at 68ch.
 *
 * Two writes on save, not one, and that is the shape of the API: the limits have
 * their own full-replace PUT precisely so that `null` on one of them is an
 * instruction and not an omission.
 */
export default function LinkEditor({
  link,
  patterns,
  globals,
  onSaved,
  onCancel,
}: {
  /** The link being edited, or null for a new one. */
  link: AdminLink | null
  patterns: AdminPattern[]
  globals: GlobalLimits
  onSaved: () => void
  onCancel: () => void
}) {
  // Taken once. The editor unmounts on a successful save — Links.tsx returns to
  // the table — so there is no saved state for this to go stale against.
  const [baseline] = useState<EditorDraft>(() => draftOf(link, patterns[0]?.id ?? ''))
  const [draft, setDraft] = useState<EditorDraft>(baseline)

  const [saving, setSaving] = useState(false)
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  const [copied, setCopied] = useState(false)

  const changed = changedCount(baseline, draft)
  // The saved slug, never the draft. The button is only rendered for a link that
  // exists, and what exists is what the server last stored — an address typed a
  // second ago resolves to nothing. The field below still shows `draft.slug`:
  // what is being typed is what the operator is deciding about, and what is
  // copied is what currently works.
  const address = window.location.origin + BOOK_PATH + (link?.slug ?? draft.slug)

  function set<K extends keyof EditorDraft>(key: K, value: EditorDraft[K]) {
    setDraft({ ...draft, [key]: value })
  }

  function limit(key: keyof LinkLimits) {
    return (value: number | null) =>
      setDraft({ ...draft, limits: { ...draft.limits, [key]: value } })
  }

  async function save() {
    setSaving(true)
    setFailure(null)

    try {
      const body = {
        slug: draft.slug,
        name: draft.name,
        description: draft.description,
        patternId: draft.patternId,
        durations: draft.durations,
      }
      const saved = link === null ? await createLink(body) : await updateLink(link.id, body)
      await updateLinkLimits(saved.id, draft.limits)
      onSaved()
    } catch (error: unknown) {
      // The server's own sentence, when it sent one. Every refusal this form can
      // provoke — a taken slug, a pattern that vanished, a duration out of range
      // — already names itself better than a generic line would.
      setSaving(false)
      setFailure(error)
    }
  }

  async function setActive(active: boolean) {
    if (link === null) return

    setSaving(true)
    setFailure(null)

    try {
      await (active ? restoreLink(link.id) : archiveLink(link.id))
      onSaved()
    } catch (error: unknown) {
      setSaving(false)
      setFailure(error)
    }
  }

  return (
    <div className="form-col">
      <div className="page-header">
        <h1 className="title-page">{link === null ? 'New link' : link.name}</h1>
        <button className="btn btn-ghost" type="button" onClick={onCancel}>
          Back
        </button>
      </div>

      <form
        onSubmit={(event) => {
          event.preventDefault()
          void save()
        }}
      >
        <div className="card card-pad stack">
          <h2 className="title-section">What it is</h2>

          {/* Stacked, not a field-row: a bare one-word label next to a 288px
              control leaves roughly 200px of dead space in the label track. A
              row only reads as intentional when the label column has something
              in it, the way Pattern's help text and the limits' do. Name has
              none, so it takes the full column, matching Address and
              Description below. */}
          <div className="field">
            <label htmlFor="link-name">Name</label>
            <input
              id="link-name"
              className="input"
              value={draft.name}
              onChange={(event) => set('name', event.target.value)}
            />
          </div>

          {/* Stacked rather than a two-column field-row: the composite here is a
              static prefix and an input, meant to span the full column the way
              Name and Description do — nothing that wide belongs beside a label.

              Copy sits on the label line rather than beside the control: once
              the row below is the prefix and the input at full width, there is
              no room left for a third element without wrapping. Right-aligned
              opposite the label, above the help text, and a sibling of the help
              paragraph rather than sharing a line with it. */}
          <div className="field">
            <div className="row row-between">
              <label htmlFor="link-slug">Address</label>
              {/* Only for a link that exists. A slug that has never been saved
                  is not an address yet, and offering to copy it hands out a 404. */}
              {link !== null && (
                <button
                  className="btn btn-ghost"
                  type="button"
                  aria-label="Copy link address"
                  onClick={() => {
                    void navigator.clipboard.writeText(address)
                    setCopied(true)
                  }}
                >
                  {copied ? 'Copied' : 'Copy'}
                </button>
              )}
            </div>
            <p className="fine">Lowercase words joined by hyphens.</p>
            {/* The whole URL, not its last word. An operator setting this is
                deciding what to paste into a chat, and they cannot judge
                `consulting` without the part in front of it. The scheme is
                dropped from what is SHOWN, as the browser's own address bar
                does — but the Copy button above copies `address`, which still
                carries it: the copied string must stay a full, working
                absolute URL.

                No fixed size class on the input: .prefix already reserves the
                static segment's intrinsic width via `flex: none` and lets the
                input's own `min-width: 0` (kairos.css, `.prefix > .input`)
                absorb the rest, so the pair ends flush with Name and
                Description rather than stopping at ctl-md's 288px partway
                through the column — and shrinks rather than overflowing on a
                narrow viewport. */}
            <span className="prefix">
              <span className="prefix-static">{window.location.host + BOOK_PATH}</span>
              <input
                id="link-slug"
                className="input"
                value={draft.slug}
                onChange={(event) => {
                  setCopied(false)
                  set('slug', event.target.value)
                }}
              />
            </span>
          </div>

          <div className="field">
            <label htmlFor="link-description">Description</label>
            <textarea
              id="link-description"
              className="textarea"
              value={draft.description}
              onChange={(event) => set('description', event.target.value)}
            />
          </div>
        </div>

        <div className="card card-pad mt-4 stack">
          <h2 className="title-section">When it can be booked</h2>

          <div className="field-row">
            <div className="field-row-label">
              <label htmlFor="link-pattern">Pattern</label>
              <p className="fine">The hours this link may be booked in, in your own zone.</p>
            </div>
            <div className="field-row-control">
              <select
                id="link-pattern"
                className="select ctl-md"
                value={draft.patternId}
                onChange={(event) => set('patternId', event.target.value)}
              >
                {patterns.map((pattern) => (
                  <option key={pattern.id} value={pattern.id}>
                    {pattern.name}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <DurationEditor
            durations={draft.durations}
            onChange={(durations) => set('durations', durations)}
          />
        </div>

        <div className="card card-pad mt-4 stack">
          <h2 className="title-section">Limits</h2>
          {/* No mb-3 here either: it would stack with `.stack`'s own margin-top
              on the rowgroup that follows, for the same double-gap reason the
              heading's mb-3 was dropped. */}
          <p className="prose t-sm">
            Each of these follows the global default until you override it here. Overriding and
            typing 0 is a real setting and not the same thing: no gap between meetings, bookable
            with no notice, nothing bookable that day.
          </p>

          <div className="rowgroup">
            {LIMIT_KEYS.map((key) => {
              const copy = LIMIT_COPY[key]
              return (
                <InheritableNumber
                  key={key}
                  label={copy.label}
                  short={copy.short}
                  help={copy.help}
                  value={draft.limits[key]}
                  globalValue={globals[key]}
                  min={copy.floor}
                  onChange={limit(key)}
                />
              )
            })}
          </div>
        </div>

        <Failure error={failure} className="mt-4" />

        <div className="savebar">
          <span className="savebar-state" role="status">
            {unsavedLabel(changed)}
          </span>
          <button
            className="btn"
            type="button"
            disabled={changed === CLEAN || saving}
            onClick={() => setDraft(baseline)}
          >
            Discard
          </button>
          <button
            className="btn btn-primary"
            type="submit"
            disabled={changed === CLEAN || saving}
            data-loading={saving || undefined}
          >
            Save link
          </button>
        </div>
      </form>

      {link !== null && (
        <div className="card card-pad mt-5">
          <h2 className="title-section mb-3">{link.active ? 'Archive' : 'Restore'}</h2>
          {/* Stated before the button rather than after the fact. Archiving is
              reversible and Restore is right here, which is why there is no
              confirmation dialog in front of it. */}
          <p className="prose t-sm mb-4">
            An archived link answers as though it never existed, and the meetings already booked
            through it are untouched.
          </p>
          {/* Rendered only while dirty: a permanent sentence about a condition
              that is usually false is noise. Without it, archiving or restoring
              calls `onSaved`, the parent unmounts this editor, and whatever the
              operator typed above is gone with no word said about it. */}
          {changed !== CLEAN && (
            <p className="prose t-sm mb-4">
              This form has unsaved changes. {link.active ? 'Archiving' : 'Restoring'} discards
              them — save or discard the changes above first.
            </p>
          )}
          {link.active ? (
            <button
              className="btn btn-danger"
              type="button"
              disabled={saving || changed !== CLEAN}
              onClick={() => void setActive(false)}
            >
              Archive link
            </button>
          ) : (
            <button
              className="btn"
              type="button"
              disabled={saving || changed !== CLEAN}
              onClick={() => void setActive(true)}
            >
              Restore link
            </button>
          )}
        </div>
      )}
    </div>
  )
}
