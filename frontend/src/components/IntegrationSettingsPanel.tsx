import { useId, useState } from 'react'

import {
  asApiError,
  saveIntegrationSettings,
  SETTING_TOO_MANY,
  SETTING_UNKNOWN,
  type IntegrationSettings,
  type SettingField,
  type SettingValue,
} from '../lib/api'

import AlertIcon from './AlertIcon'

type Draft = Record<string, SettingValue>

const UNREADABLE =
  'Kairos could not read this list from the provider just now. What is shown is what it is using, and saving here leaves it exactly as it is.'

const SAVE_FAILED = 'That could not be saved. Try again, and check the server log if it keeps happening.'

const SENT_UNKNOWN =
  'This build of Kairos sent a setting the server does not recognise. Trying again will not help; the server log names the key it refused.'

const TOO_MANY = 'You have chosen as many as this allows. Untick one to choose another.'

const UNKNOWN_KIND =
  'This build of Kairos cannot draw this setting. The server is still using the value it has, and saving here leaves it alone.'

/** The `select` value that stands for "nothing chosen", which only an optional field offers. */
const NOTHING_CHOSEN = ''

/**
 * Every setting a provider has, drawn from its description and nothing else.
 *
 * There is no branch on `settings.provider` anywhere below, and there must
 * never be one: a provider this build has never heard of renders here the same
 * as Google does, which is the whole reason the backend describes fields rather
 * than the frontend shipping a panel per vendor.
 */
export default function IntegrationSettingsPanel({
  settings,
  onSaved,
}: {
  settings: IntegrationSettings
  onSaved: () => void
}) {
  // Every DOM id below hangs off this. A field key is the provider's own word —
  // `enabled` is the reserved switch every description carries — so two panels
  // on one page, or any unrelated element that happens to use that word, would
  // silently bind a label to the wrong control.
  const prefix = useId()

  // Seeded once, because nothing replaces `settings` under a mounted panel:
  // `Integrations.tsx` closes the disclosure on a successful save, so the panel
  // unmounts and the next disclosure reads the provider again and seeds a fresh
  // draft from what came back. Re-seeding on a changed prop would be a branch
  // the application has no way to reach.
  const [draft, setDraft] = useState<Draft>(() => seed(settings))
  const [saving, setSaving] = useState(false)
  const [failure, setFailure] = useState('')

  function edit(key: string, value: SettingValue) {
    setDraft((previous) => ({ ...previous, [key]: value }))
    // A refusal describes the values that were sent. Change one and it no
    // longer does, so it goes rather than sitting red under a corrected form.
    setFailure('')
  }

  // What Save sends. A field whose list could not be read leaves the map rather
  // than blocking it: `IIntegrationSettings.SaveSettingsAsync` documents an
  // absent key as left as it was, because `IntegrationSettingStore` merges, and
  // `IntegrationSettingsWriter` hands the provider nothing at all when the
  // switch is the only key left — so a switch-only save never reaches the
  // refusal an unreachable provider would answer with. Blocking Save instead is
  // what stranded the operator: pausing a provider is what stops its lists
  // coming back, and the switch that un-pauses it lives in this form.
  const sending = settings.fields.filter((field) => !unreadable(field))

  // The cap is enforced here so that a save the server would refuse never leaves
  // the browser, and `required` is refused for exactly the same reason — but the
  // refusal that comes back cannot say which field was empty, so it is stopped
  // here too, where the field has a name to give. Asked only of what is being
  // sent: a field left out of the save has nothing to answer for, and the server
  // keeps the value it already had.
  const missing = sending.find((field) => unanswered(field, draft))

  async function save() {
    setSaving(true)
    setFailure('')

    try {
      await saveIntegrationSettings(settings.provider, payload(sending, draft))
      onSaved()
    } catch (error: unknown) {
      setFailure(refusal(asApiError(error).code))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="panel">
      {settings.fields.map((field) => renderField(field, `${prefix}-${field.key}`, draft, edit))}

      {/* No role="alert": Save is disabled while this holds and stays disabled,
          so it is a standing condition to read rather than an event to
          interrupt with. */}
      {missing !== undefined && (
        <p className="field-error mt-3">
          <AlertIcon />
          {missing.label} has to be chosen before these settings can be saved.
        </p>
      )}

      {failure !== '' && (
        <p className="field-error mt-3" role="alert">
          <AlertIcon />
          {failure}
        </p>
      )}

      <div className="panel-foot">
        <button
          className="btn btn-primary"
          type="button"
          disabled={missing !== undefined || saving}
          data-loading={saving || undefined}
          onClick={() => void save()}
        >
          Save
        </button>
      </div>
    </div>
  )
}

function seed(settings: IntegrationSettings): Draft {
  return Object.fromEntries(settings.fields.map((field) => [field.key, field.value]))
}

/** The map Save sends: the fields still worth a value, holding what was typed into them. */
function payload(fields: SettingField[], draft: Draft): Draft {
  return Object.fromEntries(fields.map((field) => [field.key, draft[field.key]]))
}

/**
 * A field offering a list the provider could not be asked for. Named kinds only:
 * a kind this build cannot draw carries no list this panel is able to judge, and
 * its value round-trips untouched.
 */
function unreadable(field: SettingField): boolean {
  return (field.kind === 'select' || field.kind === 'multiSelect') && field.options === null
}

/** What a refused save means, in the operator's terms. `code` is null for a 400 with no code. */
function refusal(code: string | null): string {
  if (code === SETTING_TOO_MANY) return TOO_MANY
  // Not "try again": every attempt sends the same key and every one is refused.
  if (code === SETTING_UNKNOWN) return SENT_UNKNOWN
  return SAVE_FAILED
}

/** A required field the operator has not answered. A toggle is answered either way. */
function unanswered(field: SettingField, draft: Draft): boolean {
  if (!field.required) return false

  const value = draft[field.key]

  if (field.kind === 'select') return value === null || value === NOTHING_CHOSEN
  if (field.kind === 'multiSelect') return ((value as string[] | null) ?? []).length === 0

  return false
}

function renderField(
  field: SettingField,
  id: string,
  draft: Draft,
  edit: (key: string, value: SettingValue) => void,
) {
  const set = (value: SettingValue) => edit(field.key, value)

  switch (field.kind) {
    case 'toggle':
      return renderToggle(field, id, draft[field.key] === true, set)
    case 'select':
      return renderSelect(field, id, (draft[field.key] as string | null) ?? NOTHING_CHOSEN, set)
    case 'multiSelect':
      return renderMultiSelect(field, id, (draft[field.key] as string[] | null) ?? [], set)
    default:
      return renderUnknownKind(field)
  }
}

function renderToggle(
  field: SettingField,
  id: string,
  checked: boolean,
  set: (value: SettingValue) => void,
) {
  return (
    <div className="field-row" key={field.key}>
      <div className="field-row-label">
        <label htmlFor={id}>{field.label}</label>
        {field.help !== null && <p className="fine">{field.help}</p>}
      </div>
      <div className="field-row-control">
        {/* Empty, and wrapping the box rather than sitting beside it. The label
            that names this field is in the grid's other track and cannot be the
            target, so this one exists only to be the target: it carries the
            44px and adds nothing to the accessible name. */}
        <label className="check-tap">
          <input
            id={id}
            type="checkbox"
            checked={checked}
            onChange={(event) => set(event.target.checked)}
          />
        </label>
      </div>
    </div>
  )
}

function renderSelect(
  field: SettingField,
  id: string,
  value: string,
  set: (value: SettingValue) => void,
) {
  const noList = unreadable(field)

  // With no list the saved value is all there is to offer, and it is offered as itself: a select
  // holding a value none of its options carries renders blank, which reads as a setting that has
  // been cleared rather than one whose choices could not be read.
  const options =
    field.options ?? (value === NOTHING_CHOSEN ? [] : [{ value, label: value, detail: null }])

  return (
    <div className="field-row" key={field.key}>
      <div className="field-row-label">
        <label htmlFor={id}>{field.label}</label>
        {field.help !== null && <p className="fine">{field.help}</p>}
        {noList && (
          <p className="field-error">
            <AlertIcon />
            {UNREADABLE}
          </p>
        )}
      </div>
      <div className="field-row-control">
        <select
          id={id}
          className="select"
          value={value}
          // Null options are a list that could not be read, not an empty one. Offering an empty
          // dropdown would invite a choice made between nothing.
          disabled={noList}
          // Out through the same door it came in through. The empty string is
          // this panel's stand-in for "nothing chosen" and is the one value a
          // select can hold that the contract does not define: every non-null
          // value it accepts is one of the field's own options.
          onChange={(event) =>
            set(event.target.value === NOTHING_CHOSEN ? null : event.target.value)
          }
        >
          {!field.required && <option value={NOTHING_CHOSEN}>None</option>}
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.detail === null ? option.label : `${option.label} — ${option.detail}`}
            </option>
          ))}
        </select>
      </div>
    </div>
  )
}

function renderMultiSelect(
  field: SettingField,
  id: string,
  chosen: string[],
  set: (value: SettingValue) => void,
) {
  // A cap that is reached disables what is not chosen and never what is — disabling a ticked box
  // is a cap the operator cannot get back under.
  const full = field.maxValues !== null && chosen.length >= field.maxValues
  const noList = unreadable(field)

  // With no list, the saved values are all there is to show, and they are shown as themselves
  // rather than hidden: they are what the backend is using right now.
  const options = field.options ?? chosen.map((value) => ({ value, label: value, detail: null }))

  // The field's name is a span rather than a label, because a group of boxes has
  // no one control to bind to. `role="group"` plus this is what gives the group
  // the name a screen reader would otherwise not read at all.
  const nameId = `${id}-name`

  function toggle(value: string, on: boolean) {
    set(on ? [...chosen, value] : chosen.filter((candidate) => candidate !== value))
  }

  return (
    <div className="field field-stack" key={field.key}>
      <span className="field-row-name" id={nameId}>
        {field.label}
      </span>
      {field.help !== null && <p className="fine">{field.help}</p>}

      <div className="check-list" role="group" aria-labelledby={nameId}>
        {options.map((option) => {
          const checked = chosen.includes(option.value)
          const capped = full && !checked
          const optionId = `${id}-${option.value}`

          return (
            <div
              className="check-row"
              key={option.value}
              // One mark for both ways a row can be dead. A cap-disabled row that
              // kept full ink beside a greyed unreadable one is two disabled
              // states with no difference the reader could infer.
              data-disabled={noList || capped || undefined}
            >
              <input
                id={optionId}
                type="checkbox"
                checked={checked}
                disabled={noList || capped}
                onChange={(event) => toggle(option.value, event.target.checked)}
              />
              <label htmlFor={optionId}>
                {option.label}
                {option.detail !== null && <span className="fine detail">{option.detail}</span>}
              </label>
            </div>
          )
        })}
      </div>

      {/* Which constraint closed the row, said where the rows are. Not shown
          beside UNREADABLE: there the list is every value already chosen, so
          the cap is not what is stopping anything. */}
      {full && !noList && <p className="fine">{TOO_MANY}</p>}

      {noList && (
        <p className="field-error">
          <AlertIcon />
          {UNREADABLE}
        </p>
      )}
    </div>
  )
}

/**
 * A kind this build has no renderer for. Self-hosted means a server can be newer
 * than the frontend bundled with it, and the alternative is a field that
 * disappears while its value still round-trips through every save.
 */
function renderUnknownKind(field: SettingField) {
  return (
    <div className="field-row" key={field.key}>
      <div className="field-row-label">
        <span className="field-row-name">{field.label}</span>
        <p className="fine">{UNKNOWN_KIND}</p>
      </div>
    </div>
  )
}
