// The settings half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured rather than
// stylistic: lib/api.ts is imported by the booking page, so anything added to
// it lands in the entry chunk and is downloaded by every invitee who opens a
// link on their phone — endpoints they can never call. This module is imported
// only by pages behind the lazily imported admin shell, so Rollup places it in
// the admin chunk. It still makes no fetch of its own: `request` is the one
// place a failure becomes an ApiError, and a second copy of that is the copy
// that gets it wrong.
//
// Named adminSettings rather than settings, like its sibling adminLinks.ts and
// for the same dull reason: Settings.tsx sits in this directory, and on a
// case-insensitive filesystem `./settings` and `./Settings` are the same name
// to the module resolver.

import { request } from '../../lib/api'

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
 * The organizer's zone, the four globals, and who is signed in.
 *
 * The four limits are plain numbers and never null: this IS the level a link's
 * null inherits from, so there is nothing above it for one of them to defer to.
 * `signedInAs` is null when the identity provider sent no name — the workbench
 * draws no identity block in that case rather than inventing one.
 */
export type Settings = {
  /** IANA id. Every time the API returns is derived from it. */
  timeZone: string
  signedInAs: string | null
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
  overrides: OverrideCounts
}

/**
 * One zone as the picker offers it.
 *
 * `offset` is what the zone reads right now — `+02:00` — and is a label rather
 * than a fact about the zone: Warsaw is `+01:00` in January. Nothing sends it
 * back; it is there because `Europe/Warsaw` means nothing to somebody scanning
 * for `+02:00`.
 */
export type TimeZoneOption = { id: string; offset: string }

/** The five values the operator sets, all of them, every time. */
export type SettingsDraft = {
  timeZoneId: string
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
}

const SETTINGS = '/admin/settings'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

export function getSettings(signal?: AbortSignal): Promise<Settings> {
  return request<Settings>(SETTINGS, { signal })
}

/** A full replace, answering with the settings as they now stand. */
export function saveSettings(draft: SettingsDraft): Promise<Settings> {
  return request<Settings>(SETTINGS, {
    method: 'PUT',
    headers: JSON_HEADERS,
    body: JSON.stringify(draft),
  })
}

/**
 * Every zone this build's TZDB carries, by id.
 *
 * Served by the backend rather than read from `Intl.supportedValuesOf`, and the
 * difference is not academic: the browser's tz database and NodaTime's are two
 * databases, so a zone this browser knows and that one does not would be
 * offered here and refused by the save.
 */
export function listTimeZones(signal?: AbortSignal): Promise<TimeZoneOption[]> {
  return request<TimeZoneOption[]>(`${SETTINGS}/zones`, { signal })
}
