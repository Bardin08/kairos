import { useState, type Dispatch, type SetStateAction } from 'react'

import {
  asApiError,
  saveIntegrationSettings,
  SETTING_TOO_MANY,
  type IntegrationSettings,
  type SettingField,
  type SettingValue,
} from '../lib/api'

import AlertIcon from './AlertIcon'

type Draft = Record<string, SettingValue>

const UNREADABLE =
  'Kairos could not read this list from the provider just now. What is shown is what it is using; nothing has changed.'

const SAVE_FAILED = 'That could not be saved. Try again, and check the server log if it keeps happening.'

const TOO_MANY = 'You have chosen as many as this allows. Untick one to choose another.'

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
  const [draft, setDraft] = useState<Draft>(() =>
    Object.fromEntries(settings.fields.map((field) => [field.key, field.value])),
  )
  const [saving, setSaving] = useState(false)
  const [failure, setFailure] = useState('')

  // A list the provider could not give us is a list nothing can be chosen from,
  // and saving would write whatever the browser happened to be holding.
  const unreadable = settings.fields.some(
    (field) => field.kind !== 'toggle' && field.options === null,
  )

  async function save() {
    setSaving(true)
    setFailure('')

    try {
      await saveIntegrationSettings(settings.provider, draft)
      onSaved()
    } catch (error: unknown) {
      const failed = asApiError(error)
      setFailure(failed.code === SETTING_TOO_MANY ? TOO_MANY : SAVE_FAILED)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="panel">
      {settings.fields.map((field) => renderField(field, draft, setDraft))}

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
          disabled={unreadable || saving}
          data-loading={saving || undefined}
          onClick={() => void save()}
        >
          Save
        </button>
      </div>
    </div>
  )
}

function renderField(field: SettingField, draft: Draft, setDraft: Dispatch<SetStateAction<Draft>>) {
  const set = (value: SettingValue) => setDraft((previous) => ({ ...previous, [field.key]: value }))

  switch (field.kind) {
    case 'toggle':
      return renderToggle(field, draft[field.key] === true, set)
    case 'select':
      return renderSelect(field, (draft[field.key] as string | null) ?? NOTHING_CHOSEN, set)
    case 'multiSelect':
      return renderMultiSelect(field, (draft[field.key] as string[] | null) ?? [], set)
  }
}

function renderToggle(field: SettingField, checked: boolean, set: (value: SettingValue) => void) {
  return (
    <div className="field-row" key={field.key}>
      <div className="field-row-label">
        <label htmlFor={field.key}>{field.label}</label>
        {field.help !== null && <p className="fine">{field.help}</p>}
      </div>
      <div className="field-row-control">
        <input
          id={field.key}
          className="check-tap"
          type="checkbox"
          checked={checked}
          onChange={(event) => set(event.target.checked)}
        />
      </div>
    </div>
  )
}

function renderSelect(field: SettingField, value: string, set: (value: SettingValue) => void) {
  // With no list the saved value is all there is to offer, and it is offered as itself: a select
  // holding a value none of its options carries renders blank, which reads as a setting that has
  // been cleared rather than one whose choices could not be read.
  const options =
    field.options ?? (value === NOTHING_CHOSEN ? [] : [{ value, label: value, detail: null }])

  return (
    <div className="field-row" key={field.key}>
      <div className="field-row-label">
        <label htmlFor={field.key}>{field.label}</label>
        {field.help !== null && <p className="fine">{field.help}</p>}
        {field.options === null && (
          <p className="field-error">
            <AlertIcon />
            {UNREADABLE}
          </p>
        )}
      </div>
      <div className="field-row-control">
        <select
          id={field.key}
          className="select"
          value={value}
          // Null options are a list that could not be read, not an empty one. Offering an empty
          // dropdown would invite a save that clears a setting nobody meant to touch.
          disabled={field.options === null}
          onChange={(event) => set(event.target.value)}
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
  chosen: string[],
  set: (value: SettingValue) => void,
) {
  // A cap that is reached disables what is not chosen and never what is — disabling a ticked box
  // is a cap the operator cannot get back under.
  const full = field.maxValues !== null && chosen.length >= field.maxValues
  const unreadable = field.options === null

  // With no list, the saved values are all there is to show, and they are shown as themselves
  // rather than hidden: they are what the backend is using right now.
  const options = field.options ?? chosen.map((value) => ({ value, label: value, detail: null }))

  function toggle(value: string, on: boolean) {
    set(on ? [...chosen, value] : chosen.filter((candidate) => candidate !== value))
  }

  return (
    <div className="field-stack" key={field.key}>
      <span className="field-row-name">{field.label}</span>
      {field.help !== null && <p className="fine">{field.help}</p>}

      <div className="check-list">
        {options.map((option) => {
          const checked = chosen.includes(option.value)
          const id = `${field.key}-${option.value}`

          return (
            <div className="check-row" key={option.value} data-disabled={unreadable || undefined}>
              <input
                id={id}
                type="checkbox"
                checked={checked}
                disabled={unreadable || (full && !checked)}
                onChange={(event) => toggle(option.value, event.target.checked)}
              />
              <label htmlFor={id}>
                {option.label}
                {option.detail !== null && <span className="detail">{option.detail}</span>}
              </label>
            </div>
          )
        })}
      </div>

      {unreadable && (
        <p className="field-error">
          <AlertIcon />
          {UNREADABLE}
        </p>
      )}
    </div>
  )
}
