// The settings half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured: lib/api.ts is in
// the booking route's entry chunk, so anything added to it is downloaded as
// endpoints the invitee can never call. This is reached only through the lazily
// imported admin shell, and still goes through `adminRequest`.
//
// Named adminSettings rather than settings: Settings.tsx sits in this directory,
// and on a case-insensitive filesystem the two are the same name.

import { adminRequest } from './session'

/**
 * How many links have written their own value for each limit, and will
 * therefore ignore a change to the global beside it.
 */
export type OverrideCounts = {
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
}

/**
 * The organizer's zone, the four globals, the proposal hold, and who is signed
 * in. The four limits are plain numbers and never null: this IS the level a
 * link's null inherits from. `signedInAs` is null when the identity provider
 * sent no name, and the workbench then draws no identity block at all.
 */
export type Settings = {
  /** IANA id. Every time the API returns is derived from it. */
  timeZone: string
  signedInAs: string | null
  /**
   * Where invitees reach this deployment, or null when the operator has configured
   * nothing. Null means "use the browser's origin", which is the right answer for a
   * deployment served under a single hostname and the wrong one for a deployment that
   * keeps its workbench off the public internet.
   */
  publicBaseUrl: string | null
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
  /** How long a proposed move holds its slot before it lapses. */
  proposalHoldHours: number
  overrides: OverrideCounts
}

/**
 * One zone as the picker offers it. `offset` is what the zone reads right now —
 * `+02:00` — and is a label rather than a fact about the zone: Warsaw is
 * `+01:00` in January. It is there because `Europe/Warsaw` means nothing to
 * somebody scanning for `+02:00`, and nothing sends it back.
 */
export type TimeZoneOption = { id: string; offset: string }

/** The six values the operator sets, all of them, every time. */
export type SettingsDraft = {
  timeZoneId: string
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
  /** How long a proposed move holds its slot before it lapses. */
  proposalHoldHours: number
}

const SETTINGS = '/admin/settings'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

export function getSettings(signal?: AbortSignal): Promise<Settings> {
  return adminRequest<Settings>(SETTINGS, { signal })
}

/** A full replace, answering with the settings as they now stand. */
export function saveSettings(draft: SettingsDraft): Promise<Settings> {
  return adminRequest<Settings>(SETTINGS, {
    method: 'PUT',
    headers: JSON_HEADERS,
    body: JSON.stringify(draft),
  })
}

/**
 * Every zone this build's TZDB carries, by id. Served by the backend rather than
 * read from `Intl.supportedValuesOf`: the browser's tz database and NodaTime's
 * are two databases, so a zone this browser knows and that one does not would be
 * offered here and refused by the save.
 */
export function listTimeZones(signal?: AbortSignal): Promise<TimeZoneOption[]> {
  return adminRequest<TimeZoneOption[]>(`${SETTINGS}/zones`, { signal })
}
