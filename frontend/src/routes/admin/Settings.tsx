import { useEffect, useId, useState } from 'react'
import { Link } from 'wouter'

import AlertIcon from '../../components/AlertIcon'
import { applyChoice, readChoice, type ThemeChoice } from '../../lib/theme'
import Failure from './Failure'
import {
  getSettings,
  listTimeZones,
  saveSettings,
  type Settings as OperatorSettings,
  type SettingsDraft,
  type TimeZoneOption,
} from './adminSettings'
import { LIMIT_COPY, LIMIT_KEYS, ZERO_FLOOR, type LimitCopy } from './limitCopy'

/**
 * Ends the Kairos session and not the identity provider's.
 *
 * A server route under a path the single page nests on, so it is reached with a
 * plain anchor: a wouter `<Link>` would be swallowed by the router and render
 * its own not-found. Signing out of Pocket ID as well would be a second
 * redirect and a second URL registered there, to log the operator out of
 * everything else they were using it for — which is not what pressing sign out
 * on a scheduling app asks for.
 */
const SIGN_OUT = '/admin/logout'

/** The three the stylesheet actually models. `system` sets no attribute at all. */
const THEMES: { value: ThemeChoice; label: string }[] = [
  { value: 'system', label: 'Match my system' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

/**
 * Not a `LimitKey`, and the difference is the point: no link overrides a hold,
 * so it has no override count and no row in the link editor. It is drawn with
 * the four because it is the same control, not the same kind of setting.
 */
const PROPOSAL_HOLD = 'proposalHoldHours'

/** Zero hours has lapsed before the invitee's mail is composed. */
const PROPOSAL_HOLD_FLOOR = 1

/** Reading order on this screen: the four a link can override, then the hold. */
const FIELD_KEYS = [...LIMIT_KEYS, PROPOSAL_HOLD] as const

type FieldKey = (typeof FIELD_KEYS)[number]

const FIELD_COPY: Record<FieldKey, LimitCopy> = {
  ...LIMIT_COPY,
  [PROPOSAL_HOLD]: {
    label: 'How long a proposed time is held',
    unit: 'hours',
    short: 'hrs',
    floor: PROPOSAL_HOLD_FLOOR,
    help: 'A proposal nobody answers releases its slot after this and the meeting stays where it is.',
  },
}

/** Held as typed, so a half-deleted field is an empty box rather than a zero. */
type Draft = Record<FieldKey, string>

type Phase = 'loading' | 'ready' | 'broken'

/**
 * A whole number at or above the floor, or null.
 *
 * `Number('')` is 0 and `Number(' 3 ')` is 3, both of which would let a field
 * the operator has emptied submit as a value they never typed. The pattern is
 * what makes "nothing" different from "zero".
 */
function whole(value: string, floor: number): number | null {
  if (!/^\d+$/.test(value.trim())) return null
  const parsed = Number(value)
  return parsed < floor ? null : parsed
}

/** `3 links override this`, or nothing at all when none do. */
function overrideNote(count: number): string | null {
  if (count === 0) return null
  return count === 1
    ? '1 link sets its own and will ignore this.'
    : `${count} links set their own and will ignore this.`
}

function draftOf(settings: OperatorSettings): Draft {
  return {
    marginMinutes: String(settings.marginMinutes),
    minNoticeMinutes: String(settings.minNoticeMinutes),
    dailyCap: String(settings.dailyCap),
    startIncrementMinutes: String(settings.startIncrementMinutes),
    proposalHoldHours: String(settings.proposalHoldHours),
  }
}

/**
 * The organizer's zone, the four global defaults, the theme, and the way to
 * Part B's integrations.
 *
 * Everything here is visited about twice a year, which is why it is one screen
 * behind one secondary destination rather than four tabs.
 */
export default function Settings() {
  const zoneId = useId()
  const themeId = useId()

  const [settings, setSettings] = useState<OperatorSettings | null>(null)
  const [zones, setZones] = useState<TimeZoneOption[]>([])
  const [zone, setZone] = useState('')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [theme, setTheme] = useState<ThemeChoice>(readChoice)
  const [phase, setPhase] = useState<Phase>('loading')
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    const controller = new AbortController()

    getSettings(controller.signal)
      .then((read) => {
        setSettings(read)
        setZone(read.timeZone)
        setDraft(draftOf(read))
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setPhase('broken')
        setFailure(error)
      })

    // Its own request, and its own failure: the picker's options are every zone
    // there is, and the page is still usable without them — the current zone
    // stays selected and the four defaults still save.
    listTimeZones(controller.signal)
      .then(setZones)
      .catch(() => {
        // The select falls back to the one option it already knows.
      })

    return () => controller.abort()
  }, [])

  function change(key: FieldKey, value: string) {
    setSaved(false)
    setDraft((current) => (current === null ? current : { ...current, [key]: value }))
  }

  function chooseTheme(choice: ThemeChoice) {
    setTheme(choice)
    // Painted immediately rather than on save: this one is a preference of this
    // browser and never reaches the server, so there is nothing to submit.
    applyChoice(choice)
  }

  const parsed =
    draft === null
      ? null
      : FIELD_KEYS.reduce<Partial<Record<FieldKey, number | null>>>(
          (all, key) => ({ ...all, [key]: whole(draft[key], FIELD_COPY[key].floor) }),
          {},
        )

  const complete =
    parsed !== null && FIELD_KEYS.every((key) => parsed[key] !== null && parsed[key] !== undefined)

  function save() {
    if (draft === null || parsed === null || !complete) return

    const body: SettingsDraft = {
      timeZoneId: zone,
      marginMinutes: parsed.marginMinutes as number,
      minNoticeMinutes: parsed.minNoticeMinutes as number,
      dailyCap: parsed.dailyCap as number,
      startIncrementMinutes: parsed.startIncrementMinutes as number,
      proposalHoldHours: parsed.proposalHoldHours as number,
    }

    setSaving(true)
    setFailure(null)
    setSaved(false)

    saveSettings(body)
      .then((written) => {
        // Replaced with the server's answer rather than assumed: the override
        // counts move when a link changes in another tab, and this is the read
        // that notices.
        setSettings(written)
        setZone(written.timeZone)
        setDraft(draftOf(written))
        setSaving(false)
        setSaved(true)
      })
      .catch((error: unknown) => {
        setSaving(false)
        setFailure(error)
      })
  }

  // The zone is always an option of the select, even before the list arrives
  // and even if it never does — a control that cannot show its own value reads
  // as a control that has lost it.
  const options = zones.length > 0 ? zones : [{ id: zone, offset: '' }]

  return (
    <div className="form-col">
      <div className="page-header">
        <h1 className="title-page">Settings</h1>
        <button
          className="btn btn-primary"
          type="button"
          aria-disabled={!complete || saving || phase !== 'ready' ? true : undefined}
          data-loading={saving || undefined}
          onClick={save}
        >
          Save
        </button>
      </div>

      <Failure error={failure} className="mb-5" />

      {saved && (
        <p className="status mb-5" data-state="live" role="status">
          Saved.
        </p>
      )}

      {phase === 'loading' ? (
        <p className="prose t-sm">Reading your settings…</p>
      ) : settings === null || draft === null ? null : (
        <>
          <div className="card card-pad">
            <h2 className="title-section mb-3">Time zone</h2>
            <div className="field">
              <label htmlFor={zoneId}>The zone your availability is written in</label>
              <select
                className="select ctl-lg"
                id={zoneId}
                value={zone}
                onChange={(event) => {
                  setSaved(false)
                  setZone(event.target.value)
                }}
              >
                {options.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.offset === '' ? option.id : `${option.id} · ${option.offset}`}
                  </option>
                ))}
              </select>
            </div>
            {/* Said next to the control, because "did I just move all my
                meetings?" is the obvious fear and the answer is no. */}
            <p className="fine mt-3">
              Changing this moves no meeting. Every booking is stored as an instant and stays
              exactly where it is — what changes is the wall clock your availability rules are read
              in, and therefore the hours offered from now on.
            </p>
          </div>

          <div className="page-header section-break">
            <h2 className="title-section">Global defaults</h2>
          </div>
          <div className="card card-pad">
            <div className="rowgroup">
              {FIELD_KEYS.map((key) => {
                const limit = FIELD_COPY[key]
                const value = draft[key]
                const invalid = whole(value, limit.floor) === null
                // Nothing overrides a hold, so there is no count beside it.
                const note = key === PROPOSAL_HOLD ? null : overrideNote(settings.overrides[key])

                return (
                  <div className="field-row" data-invalid={invalid ? 'true' : undefined} key={key}>
                    <div className="field-row-label">
                      <label htmlFor={`${zoneId}-${key}`}>{limit.label}</label>
                      <p className="fine">
                        {limit.help}
                        {note === null ? '' : ` ${note}`}
                      </p>
                      {invalid && (
                        <p className="field-error" role="alert">
                          <AlertIcon />
                          {limit.floor === ZERO_FLOOR
                            ? `A whole number of ${limit.unit}, and never below zero.`
                            : `A whole number of ${limit.unit}, and at least ${limit.floor}.`}
                        </p>
                      )}
                    </div>
                    <div className="field-row-control">
                      <span className="affix">
                        <input
                          className="input numeric ctl-num"
                          id={`${zoneId}-${key}`}
                          type="number"
                          inputMode="numeric"
                          min={limit.floor}
                          value={value}
                          onChange={(event) => change(key, event.target.value)}
                        />
                        <span className="affix-unit" aria-hidden="true">
                          {limit.short}
                        </span>
                      </span>
                    </div>
                  </div>
                )
              })}
            </div>
          </div>

          <div className="page-header section-break">
            <h2 className="title-section">Appearance</h2>
          </div>
          <div className="card card-pad">
            <div className="field">
              <label htmlFor={themeId}>Theme</label>
              {/* Three states and not two. An icon that flips between light and
                  dark cannot express "whatever this device is set to", which is
                  the default and the one most people should stay on — so the
                  control that would lose it is not the control this page uses. */}
              <select
                className="select ctl-md"
                id={themeId}
                value={theme}
                onChange={(event) => chooseTheme(event.target.value as ThemeChoice)}
              >
                {THEMES.map((choice) => (
                  <option key={choice.value} value={choice.value}>
                    {choice.label}
                  </option>
                ))}
              </select>
            </div>
            <p className="fine mt-3">
              This one is a preference of this browser and is not saved to the server. Light and
              dark override the system setting in both directions; matching it follows the device.
            </p>
          </div>

          <div className="page-header section-break">
            <h2 className="title-section">Session</h2>
          </div>
          <div className="card card-pad">
            <p className="prose t-sm mb-4">
              {settings.signedInAs === null
                ? 'Signed in. Your identity provider sent no name, so there is none to show.'
                : `Signed in as ${settings.signedInAs}.`}{' '}
              Signing out ends this Kairos session and leaves your identity provider alone.
            </p>
            <a className="btn" href={SIGN_OUT}>
              Sign out
            </a>
          </div>

          <div className="page-header section-break">
            <h2 className="title-section">Integrations</h2>
          </div>
          <div className="card card-pad">
            <p className="prose t-sm mb-4">
              Connect a calendar and Kairos reads the busy time it already holds, so a slot it
              offers is a slot you are actually free for.
            </p>
            {/* `~` escapes the shell's `/admin` base. Without it wouter would
                resolve this to /admin/integrations, which is nothing. */}
            <Link className="btn" href="~/integrations">
              Open integrations
            </Link>
          </div>
        </>
      )}
    </div>
  )
}
