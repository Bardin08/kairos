import { useState } from 'react'

import AlertIcon from '../../components/AlertIcon'
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
import { asApiError } from '../../lib/api'

/** The four limits of a link that does not exist yet: all inherited. */
const NEW_LIMITS: LinkLimits = {
  marginMinutes: null,
  minNoticeMinutes: null,
  dailyCap: null,
  startIncrementMinutes: null,
}

/** What a new link starts as. One duration, because zero is not a link. */
const NEW_DURATIONS = [30]

/** The floor for three of the four. See InheritableNumber's `min`. */
const NO_FLOOR = 0

/**
 * The start increment's floor. Zero there is not a smaller step, it is the step
 * that makes the generator's `PlusMinutes(step)` never advance.
 */
const INCREMENT_FLOOR = 1

/**
 * One link, in full: what it is called, where it lives, how long it runs, which
 * hours it may be booked in, and which of the four global limits it narrows.
 *
 * Two writes on save, not one, and that is the shape of the API rather than an
 * accident: the limits have their own full-replace PUT precisely so that `null`
 * on one of them is an instruction and not an omission.
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
  const [name, setName] = useState(link?.name ?? '')
  const [slug, setSlug] = useState(link?.slug ?? '')
  const [description, setDescription] = useState(link?.description ?? '')
  const [patternId, setPatternId] = useState(link?.patternId ?? patterns[0]?.id ?? '')
  const [durations, setDurations] = useState<number[]>(link?.durations ?? NEW_DURATIONS)
  // Copied out field by field rather than by spreading the link, so a null here
  // is this component's own null and never a value some later edit resolved.
  const [limits, setLimits] = useState<LinkLimits>(
    link === null
      ? NEW_LIMITS
      : {
          marginMinutes: link.marginMinutes,
          minNoticeMinutes: link.minNoticeMinutes,
          dailyCap: link.dailyCap,
          startIncrementMinutes: link.startIncrementMinutes,
        },
  )

  const [saving, setSaving] = useState(false)
  const [failure, setFailure] = useState('')

  function limit(key: keyof LinkLimits) {
    return (value: number | null) => setLimits({ ...limits, [key]: value })
  }

  async function save() {
    setSaving(true)
    setFailure('')

    try {
      const draft = { slug, name, description, patternId, durations }
      const saved = link === null ? await createLink(draft) : await updateLink(link.id, draft)
      await updateLinkLimits(saved.id, limits)
      onSaved()
    } catch (error: unknown) {
      // The server's own sentence, when it sent one. Every refusal this form can
      // provoke — a taken slug, a pattern that vanished, a duration out of range
      // — already names itself better than a generic line would.
      setSaving(false)
      setFailure(asApiError(error).message)
    }
  }

  async function setActive(active: boolean) {
    if (link === null) return

    setSaving(true)
    setFailure('')

    try {
      await (active ? restoreLink(link.id) : archiveLink(link.id))
      onSaved()
    } catch (error: unknown) {
      setSaving(false)
      setFailure(asApiError(error).message)
    }
  }

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">{link === null ? 'New link' : link.name}</h1>
        <button className="btn btn-ghost" type="button" onClick={onCancel}>
          Back
        </button>
      </div>

      <form
        className="card card-pad stack"
        onSubmit={(event) => {
          event.preventDefault()
          void save()
        }}
      >
        <div className="field">
          <label htmlFor="link-name">Name</label>
          <input
            id="link-name"
            className="input"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <div className="field">
          <label htmlFor="link-slug">Address</label>
          <input
            id="link-slug"
            className="input"
            value={slug}
            onChange={(event) => setSlug(event.target.value)}
          />
          <p className="fine">
            The last part of the URL you send people: lowercase words joined by hyphens.
          </p>
        </div>

        <div className="field">
          <label htmlFor="link-description">Description</label>
          <textarea
            id="link-description"
            className="textarea"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </div>

        <div className="field">
          <label htmlFor="link-pattern">Pattern</label>
          <select
            id="link-pattern"
            className="select"
            value={patternId}
            onChange={(event) => setPatternId(event.target.value)}
          >
            {patterns.map((pattern) => (
              <option key={pattern.id} value={pattern.id}>
                {pattern.name}
              </option>
            ))}
          </select>
          <p className="fine">The hours this link may be booked in.</p>
        </div>

        <DurationEditor durations={durations} onChange={setDurations} />

        <h2 className="title-section section-break">Limits</h2>
        <p className="prose t-sm">
          Each of these follows the global default until you turn it off here. Turning it off and
          typing 0 is a real setting and not the same thing: no gap between meetings, bookable with
          no notice, nothing bookable that day.
        </p>

        <InheritableNumber
          label="Margin between meetings"
          unit="minutes"
          value={limits.marginMinutes}
          globalValue={globals.marginMinutes}
          min={NO_FLOOR}
          onChange={limit('marginMinutes')}
        />
        <InheritableNumber
          label="Minimum notice"
          unit="minutes"
          value={limits.minNoticeMinutes}
          globalValue={globals.minNoticeMinutes}
          min={NO_FLOOR}
          onChange={limit('minNoticeMinutes')}
        />
        <InheritableNumber
          label="Booking cap"
          unit="a day"
          value={limits.dailyCap}
          globalValue={globals.dailyCap}
          min={NO_FLOOR}
          onChange={limit('dailyCap')}
        />
        <InheritableNumber
          label="Start increment"
          unit="minutes"
          value={limits.startIncrementMinutes}
          globalValue={globals.startIncrementMinutes}
          min={INCREMENT_FLOOR}
          onChange={limit('startIncrementMinutes')}
        />

        {failure !== '' && (
          <p className="field-error" role="alert">
            <AlertIcon />
            {failure}
          </p>
        )}

        <div className="row gap-2 wrap">
          <button className="btn btn-primary" type="submit" data-loading={saving || undefined}>
            Save link
          </button>
          {link !== null &&
            (link.active ? (
              <button
                className="btn btn-danger"
                type="button"
                disabled={saving}
                onClick={() => void setActive(false)}
              >
                Archive link
              </button>
            ) : (
              <button
                className="btn"
                type="button"
                disabled={saving}
                onClick={() => void setActive(true)}
              >
                Restore link
              </button>
            ))}
        </div>

        {link !== null && !link.active && (
          <p className="fine">
            Archived: this address answers as though it never existed, and the meetings already
            booked through it are untouched.
          </p>
        )}
      </form>
    </>
  )
}
