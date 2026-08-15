import { useEffect, useId, useState } from 'react'
import { Link } from 'wouter'

import AlertIcon from '../../components/AlertIcon'
import { asApiError } from '../../lib/api'
import { applyChoice, readChoice, type ThemeChoice } from '../../lib/theme'
import {
  getSettings,
  listTimeZones,
  saveSettings,
  type Settings as OperatorSettings,
  type SettingsDraft,
  type TimeZoneOption,
} from './adminSettings'

/**
 * The four globals, in the order the comp reads them, with the floor each one
 * answers to.
 *
 * Plain numbers, and deliberately NOT `InheritableNumber`: this is what every
 * link's null inherits from, so a control offering to inherit here would imply
 * a level above the global that does not exist.
 *
 * `floor` is not decoration on two of them. A negative margin inverts the
 * padded busy interval and starts creating overlaps that look like
 * availability; a zero start increment makes the slot generator's minute step
 * never advance. The backend refuses both — this is the same rule said early
 * enough that the operator does not have to submit to learn it.
 */
const LIMITS = [
  {
    key: 'marginMinutes',
    label: 'Margin between meetings',
    unit: 'minutes',
    floor: 0,
    help: 'Kept clear on both sides of every booking.',
  },
  {
    key: 'minNoticeMinutes',
    label: 'Minimum notice',
    unit: 'minutes',
    floor: 0,
    help: 'How far ahead the earliest bookable slot sits. Zero is bookable immediately.',
  },
  {
    key: 'dailyCap',
    label: 'Bookings a day',
    unit: 'a day',
    floor: 0,
    help: 'The most meetings one day may take. Zero closes every day.',
  },
  {
    key: 'startIncrementMinutes',
    label: 'Start increment',
    unit: 'minutes',
    floor: 1,
    help: 'The grid starts land on — 30 gives you :00 and :30.',
  },
] as const

type LimitKey = (typeof LIMITS)[number]['key']

/** The three the stylesheet actually models. `system` sets no attribute at all. */
const THEMES: { value: ThemeChoice; label: string }[] = [
  { value: 'system', label: 'Match my system' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

/** Held as typed, so a half-deleted field is an empty box rather than a zero. */
type Draft = Record<LimitKey, string>

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
  const [failure, setFailure] = useState('')
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
        setFailure(asApiError(error).message)
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

  function change(key: LimitKey, value: string) {
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
      : LIMITS.reduce<Partial<Record<LimitKey, number | null>>>(
          (all, limit) => ({ ...all, [limit.key]: whole(draft[limit.key], limit.floor) }),
          {},
        )

  const complete =
    parsed !== null && LIMITS.every((limit) => parsed[limit.key] !== null && parsed[limit.key] !== undefined)

  function save() {
    if (draft === null || parsed === null || !complete) return

    const body: SettingsDraft = {
      timeZoneId: zone,
      marginMinutes: parsed.marginMinutes as number,
      minNoticeMinutes: parsed.minNoticeMinutes as number,
      dailyCap: parsed.dailyCap as number,
      startIncrementMinutes: parsed.startIncrementMinutes as number,
    }

    setSaving(true)
    setFailure('')
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
        setFailure(asApiError(error).message)
      })
  }

  // The zone is always an option of the select, even before the list arrives
  // and even if it never does — a control that cannot show its own value reads
  // as a control that has lost it.
  const options = zones.length > 0 ? zones : [{ id: zone, offset: '' }]

  return (
    <>
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

      {failure !== '' && (
        <p className="field-error mb-5" role="alert">
          <AlertIcon />
          {failure}
        </p>
      )}

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
                className="select"
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
          <div className="card card-pad stack stack-lg">
            {LIMITS.map((limit) => {
              const value = draft[limit.key]
              const invalid = whole(value, limit.floor) === null
              const note = overrideNote(settings.overrides[limit.key])

              return (
                <div className="field" data-invalid={invalid ? 'true' : undefined} key={limit.key}>
                  <label htmlFor={`${zoneId}-${limit.key}`}>{limit.label}</label>
                  <input
                    className="input numeric"
                    id={`${zoneId}-${limit.key}`}
                    type="number"
                    inputMode="numeric"
                    min={limit.floor}
                    value={value}
                    onChange={(event) => change(limit.key, event.target.value)}
                  />
                  {invalid ? (
                    <p className="field-error" role="alert">
                      <AlertIcon />
                      {limit.floor === 0
                        ? `A whole number of ${limit.unit}, and never below zero.`
                        : `A whole number of ${limit.unit}, and at least ${limit.floor}.`}
                    </p>
                  ) : (
                    <p className="fine">
                      {limit.help}
                      {note === null ? '' : ` ${note}`}
                    </p>
                  )}
                </div>
              )
            })}
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
                className="select"
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
    </>
  )
}
