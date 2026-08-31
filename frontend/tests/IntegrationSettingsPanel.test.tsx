import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import IntegrationSettingsPanel from '../src/components/IntegrationSettingsPanel'
import {
  ApiError,
  saveIntegrationSettings,
  SETTING_TOO_MANY,
  SETTING_UNKNOWN,
} from '../src/lib/api'
import type { IntegrationSettings, SettingField } from '../src/lib/api'

// ApiError stays real: the panel reads a refusal through `asApiError`, which
// branches on `instanceof`, and a plain object would take the "could not reach
// the server" path while the code branch under test never ran.
vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  saveIntegrationSettings: vi.fn(),
}))

const PROVIDER = 'anything'
const SWITCH_LABEL = 'Use this integration'
const MULTI_LABEL = 'Read busy time from'
const SELECT_LABEL = 'Write bookings to'
const FIRST_OPTION = 'Operator'
const SECOND_OPTION = 'Side projects'
const FIRST_VALUE = 'first'
const SECOND_VALUE = 'second'
const SWITCH_KEY = 'enabled'
const MULTI_KEY = 'someSet'
const SELECT_KEY = 'someChoice'
const UNREACHABLE = /could not/i
const AS_MANY_AS_ALLOWED = /as many as this allows/i
const HAS_TO_BE_CHOSEN = /has to be chosen/i
const CANNOT_DRAW = /cannot draw this setting/i
const NOT_RECOGNISED = /does not recognise/i
const MAX_VALUES = 1
const BAD_REQUEST = 400
const SERVER_SENTENCE = 'Too many.'

/** No provider name anywhere in it — which is the property under test. */
const settings: IntegrationSettings = {
  provider: PROVIDER,
  fields: [
    {
      key: SWITCH_KEY,
      kind: 'toggle',
      label: SWITCH_LABEL,
      help: null,
      options: null,
      maxValues: null,
      required: false,
      value: true,
    },
    {
      key: MULTI_KEY,
      kind: 'multiSelect',
      label: MULTI_LABEL,
      help: null,
      options: [
        { value: FIRST_VALUE, label: FIRST_OPTION, detail: 'Primary' },
        { value: SECOND_VALUE, label: SECOND_OPTION, detail: null },
      ],
      maxValues: null,
      required: false,
      value: [FIRST_VALUE],
    },
    {
      key: SELECT_KEY,
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

/** The same description with some fields replaced, so a fixture states only its difference. */
function withFields(changes: Record<string, Partial<SettingField>>): IntegrationSettings {
  return {
    ...settings,
    fields: settings.fields.map((field) => ({ ...field, ...changes[field.key] })),
  }
}

function withField(key: string, change: Partial<SettingField>): IntegrationSettings {
  return withFields({ [key]: change })
}

/**
 * The state an operator is stranded in: the integration is off, so nothing can
 * be asked of the provider, so neither list came back — and the required field
 * was never answered, because it never had a list to answer from.
 */
const pausedAndUnreachable = withFields({
  [SWITCH_KEY]: { value: false },
  [MULTI_KEY]: { options: null },
  [SELECT_KEY]: { options: null, value: null },
})

beforeEach(() => {
  vi.mocked(saveIntegrationSettings).mockReset()
  vi.mocked(saveIntegrationSettings).mockResolvedValue(undefined)
})

afterEach(cleanup)

/**
 * This project has no jest-dom, so `checked`, `disabled` and `value` are read off
 * the element itself — the idiom the rest of the suite already uses.
 */
function control(label: string | RegExp) {
  return screen.getByLabelText(label) as HTMLInputElement
}

function saveButton() {
  return screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement
}

describe('IntegrationSettingsPanel', () => {
  it('draws every kind from the description alone', () => {
    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    expect(control(SWITCH_LABEL).checked).toBe(true)
    expect(control(new RegExp(FIRST_OPTION)).checked).toBe(true)
    expect(control(new RegExp(SECOND_OPTION)).checked).toBe(false)
    expect((screen.getByLabelText(SELECT_LABEL) as HTMLSelectElement).value).toBe(FIRST_VALUE)
  })

  // The label naming this field is in the grid's other track, so the box's own
  // 44px comes from an empty label wrapping it. Breaks if the box loses that
  // wrapper — on every kind: toggle, not on the key `enabled`, because a
  // provider's own toggles need the target as much as the universal switch
  // does. The rule sizing it is pinned in stylesheet.test.ts, jsdom performing
  // no layout.
  it('wraps every toggle in the label that carries its tap floor, whatever the provider called it', () => {
    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    const wrapper = control(SWITCH_LABEL).closest('label')

    expect(wrapper?.classList.contains('check-tap')).toBe(true)
    // Empty, so the field's own label is the whole accessible name — which is
    // what `control` above just resolved it by.
    expect(wrapper?.textContent).toBe('')
  })

  it('gives the option list a name a screen reader can read', () => {
    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    expect(screen.getByRole('group', { name: MULTI_LABEL })).not.toBeNull()
  })

  it('says a list could not be read rather than showing it as empty', () => {
    render(
      <IntegrationSettingsPanel
        settings={withField(MULTI_KEY, { options: null })}
        onSaved={() => {}}
      />,
    )

    expect(screen.getByText(UNREACHABLE)).not.toBeNull()
    // The saved value is still what the backend is using, so it is still shown.
    expect(control(new RegExp(FIRST_VALUE)).checked).toBe(true)
    // Its own boxes are dead, because there is no list to choose from — and
    // nothing else is: the switch is exactly what an operator reaches for when a
    // provider is misbehaving, and a Save it cannot press is no switch at all.
    expect(control(new RegExp(FIRST_VALUE)).disabled).toBe(true)
    expect(control(SWITCH_LABEL).disabled).toBe(false)
    expect(saveButton().disabled).toBe(false)
  })

  // The defect this file documented and passed over: pausing Google is what
  // stops its lists coming back, so the panel that turned the integration off
  // was the panel that could no longer turn it on. A field with no list opts out
  // of the save instead of blocking it — absent keys are left as they were,
  // which `saveIntegrationSettings` states and the store implements.
  it('un-pauses a provider whose lists could not be read, sending the switch alone', async () => {
    render(<IntegrationSettingsPanel settings={pausedAndUnreachable} onSaved={() => {}} />)

    expect(control(SWITCH_LABEL).checked).toBe(false)
    // Nothing is being sent for the required field, so there is nothing it has
    // to answer: the value the server holds is the value it keeps.
    expect(screen.queryByText(HAS_TO_BE_CHOSEN)).toBeNull()
    expect(saveButton().disabled).toBe(false)

    fireEvent.click(control(SWITCH_LABEL))
    fireEvent.click(saveButton())

    await waitFor(() =>
      expect(vi.mocked(saveIntegrationSettings)).toHaveBeenCalledWith(PROVIDER, {
        [SWITCH_KEY]: true,
      }),
    )
  })

  it('disables the unchosen once the cap is reached', () => {
    render(
      <IntegrationSettingsPanel
        settings={withField(MULTI_KEY, { maxValues: MAX_VALUES })}
        onSaved={() => {}}
      />,
    )

    expect(control(new RegExp(SECOND_OPTION)).disabled).toBe(true)
    expect(control(new RegExp(FIRST_OPTION)).disabled).toBe(false)
  })

  // The cap is reached by ticking rather than by arriving at it, which is the
  // path a render-time assertion cannot see: `toggle` appending to the wrong
  // array leaves every first-render assertion in this file passing.
  it('closes the cap as the operator reaches it, and says which constraint closed it', () => {
    render(
      <IntegrationSettingsPanel
        settings={withField(MULTI_KEY, { maxValues: MAX_VALUES, value: [] })}
        onSaved={() => {}}
      />,
    )

    expect(control(new RegExp(SECOND_OPTION)).disabled).toBe(false)
    expect(screen.queryByText(AS_MANY_AS_ALLOWED)).toBeNull()

    fireEvent.click(control(new RegExp(FIRST_OPTION)))

    expect(control(new RegExp(FIRST_OPTION)).checked).toBe(true)
    expect(control(new RegExp(SECOND_OPTION)).disabled).toBe(true)
    expect(screen.getByText(AS_MANY_AS_ALLOWED)).not.toBeNull()
  })

  it('sends the whole map, with a cleared optional select as null rather than an empty string', async () => {
    render(
      <IntegrationSettingsPanel
        settings={withField(SELECT_KEY, { required: false })}
        onSaved={() => {}}
      />,
    )

    fireEvent.click(control(SWITCH_LABEL))
    fireEvent.click(control(new RegExp(SECOND_OPTION)))
    fireEvent.change(screen.getByLabelText(SELECT_LABEL), { target: { value: '' } })
    fireEvent.click(saveButton())

    await waitFor(() =>
      expect(vi.mocked(saveIntegrationSettings)).toHaveBeenCalledWith(PROVIDER, {
        [SWITCH_KEY]: false,
        [MULTI_KEY]: [FIRST_VALUE, SECOND_VALUE],
        [SELECT_KEY]: null,
      }),
    )
  })

  it('says the cap was the reason when the server is the one that refuses', async () => {
    vi.mocked(saveIntegrationSettings).mockRejectedValue(
      new ApiError(BAD_REQUEST, SETTING_TOO_MANY, SERVER_SENTENCE),
    )

    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    fireEvent.click(saveButton())

    expect(await screen.findByText(AS_MANY_AS_ALLOWED)).not.toBeNull()
    // Gone the moment the form no longer describes what was refused.
    fireEvent.click(control(new RegExp(SECOND_OPTION)))
    expect(screen.queryByText(AS_MANY_AS_ALLOWED)).toBeNull()
  })

  it('names the empty required field instead of letting the server refuse the save', () => {
    render(
      <IntegrationSettingsPanel settings={withField(SELECT_KEY, { value: null })} onSaved={() => {}} />,
    )

    expect(screen.getByText(HAS_TO_BE_CHOSEN).textContent).toContain(SELECT_LABEL)
    expect(saveButton().disabled).toBe(true)
  })

  // A self-hosted server can be newer than the frontend shipped with it, and a
  // field that vanishes is a value the operator cannot see themselves saving.
  it('names a kind it cannot draw rather than dropping the field', () => {
    render(
      <IntegrationSettingsPanel
        settings={withField(SELECT_KEY, { kind: 'colour' as SettingField['kind'] })}
        onSaved={() => {}}
      />,
    )

    expect(screen.getByText(CANNOT_DRAW)).not.toBeNull()
    expect(screen.getByText(SELECT_LABEL)).not.toBeNull()
  })

  // Two refusals the operator can do nothing about would read alike under one
  // sentence, and the generic one tells them to try again — which is the wrong
  // instruction for a key the server will refuse every time it is sent.
  it('tells a build mismatch apart from a value the server rejected', async () => {
    vi.mocked(saveIntegrationSettings).mockRejectedValue(
      new ApiError(BAD_REQUEST, SETTING_UNKNOWN, SERVER_SENTENCE),
    )

    render(<IntegrationSettingsPanel settings={settings} onSaved={() => {}} />)

    fireEvent.click(saveButton())

    expect(await screen.findByText(NOT_RECOGNISED)).not.toBeNull()
  })
})
