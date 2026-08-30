import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import IntegrationSettingsPanel from '../src/components/IntegrationSettingsPanel'
import type { IntegrationSettings } from '../src/lib/api'

const PROVIDER = 'anything'
const SWITCH_LABEL = 'Use this integration'
const MULTI_LABEL = 'Read busy time from'
const SELECT_LABEL = 'Write bookings to'
const FIRST_OPTION = 'Operator'
const SECOND_OPTION = 'Side projects'
const FIRST_VALUE = 'first'
const UNREACHABLE = /could not/i
const MAX_VALUES = 1

/** No provider name anywhere in it — which is the property under test. */
const settings: IntegrationSettings = {
  provider: PROVIDER,
  fields: [
    {
      key: 'enabled',
      kind: 'toggle',
      label: SWITCH_LABEL,
      help: null,
      options: null,
      maxValues: null,
      required: false,
      value: true,
    },
    {
      key: 'someSet',
      kind: 'multiSelect',
      label: MULTI_LABEL,
      help: null,
      options: [
        { value: FIRST_VALUE, label: FIRST_OPTION, detail: 'Primary' },
        { value: 'second', label: SECOND_OPTION, detail: null },
      ],
      maxValues: null,
      required: false,
      value: [FIRST_VALUE],
    },
    {
      key: 'someChoice',
      kind: 'select',
      label: SELECT_LABEL,
      help: null,
      options: [{ value: FIRST_VALUE, label: FIRST_OPTION, detail: null }],
      maxValues: null,
      required: true,
      value: FIRST_VALUE,
    },
  ],
}

afterEach(cleanup)

/**
 * This project has no jest-dom, so `checked`, `disabled` and `value` are read off
 * the element itself — the idiom the rest of the suite already uses.
 */
function control(label: string | RegExp) {
  return screen.getByLabelText(label) as HTMLInputElement
}

describe('IntegrationSettingsPanel', () => {
  it('draws every kind from the description alone', () => {
    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    expect(control(SWITCH_LABEL).checked).toBe(true)
    expect(control(new RegExp(FIRST_OPTION)).checked).toBe(true)
    expect(control(new RegExp(SECOND_OPTION)).checked).toBe(false)
    expect((screen.getByLabelText(SELECT_LABEL) as HTMLSelectElement).value).toBe(FIRST_VALUE)
  })

  // Breaks if the box loses the class its tap floor is keyed on. A UA checkbox
  // draws at ~13px and the label naming this field is in the grid's other track,
  // so the 44px DESIGN.md requires is grown off the box by `.check-tap` — on
  // every kind: toggle, not on the key `enabled`, because a provider's own
  // toggles need the target as much as the universal switch does. The rule
  // itself is pinned in stylesheet.test.ts, jsdom performing no layout.
  it('keys every toggle to the tap-floor class, whatever the provider called it', () => {
    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    expect(control(SWITCH_LABEL).classList.contains('check-tap')).toBe(true)
  })

  it('says a list could not be read rather than showing it as empty', () => {
    const unreachable: IntegrationSettings = {
      ...settings,
      fields: settings.fields.map((field) =>
        field.kind === 'multiSelect' ? { ...field, options: null } : field,
      ),
    }

    render(<IntegrationSettingsPanel settings={unreachable} onSaved={() => {}} />)

    expect(screen.getByText(UNREACHABLE)).not.toBeNull()
    // The saved value is still what the backend is using, so it is still shown.
    expect(control(new RegExp(FIRST_VALUE)).checked).toBe(true)
    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true)
    // The switch is exactly what an operator reaches for when a provider is
    // misbehaving, so it stays live while the rest of the panel cannot save.
    expect(control(SWITCH_LABEL).disabled).toBe(false)
  })

  it('disables the unchosen once the cap is reached', () => {
    const capped: IntegrationSettings = {
      ...settings,
      fields: settings.fields.map((field) =>
        field.kind === 'multiSelect' ? { ...field, maxValues: MAX_VALUES } : field,
      ),
    }

    render(<IntegrationSettingsPanel settings={capped} onSaved={() => {}} />)

    expect(control(new RegExp(SECOND_OPTION)).disabled).toBe(true)
    expect(control(new RegExp(FIRST_OPTION)).disabled).toBe(false)
  })
})
