import type { AdminLink, LinkLimits } from './adminLinks'
import { LIMIT_KEYS } from './limitCopy'

/**
 * The editor's working copy of everything the form can change: the five fields
 * `adminLinks`' `LinkDraft` also carries, plus the four limits it does not.
 * That other `LinkDraft` is a request body — the subset the API accepts on
 * create and update. This one is form state, wider by four fields and never
 * sent as-is. `active` has its own route and belongs to neither.
 */
export type EditorDraft = {
  name: string
  slug: string
  description: string
  patternId: string
  durations: number[]
  limits: LinkLimits
}

/** The four limits of a link that does not exist yet: all inherited. */
const NEW_LIMITS: LinkLimits = {
  marginMinutes: null,
  minNoticeMinutes: null,
  dailyCap: null,
  startIncrementMinutes: null,
}

/** What a new link starts as. One duration, because zero is not a link. */
const NEW_DURATION_MINUTES = 30
const NEW_DURATIONS = [NEW_DURATION_MINUTES]

/** Nothing has moved. Named because it is a state, not an arithmetic zero. */
const CLEAN = 0
const ONE_FIELD = 1

/**
 * The link as a draft, or the shape a new one starts in.
 *
 * The limits are copied out field by field rather than by spreading the link,
 * so a null here is this module's own null and never a value some later edit
 * resolved.
 */
export function draftOf(link: AdminLink | null, firstPatternId: string): EditorDraft {
  if (link === null) {
    return {
      name: '',
      slug: '',
      description: '',
      patternId: firstPatternId,
      durations: [...NEW_DURATIONS],
      limits: { ...NEW_LIMITS },
    }
  }

  return {
    name: link.name,
    slug: link.slug,
    description: link.description,
    patternId: link.patternId,
    durations: [...link.durations],
    limits: {
      marginMinutes: link.marginMinutes,
      minNoticeMinutes: link.minNoticeMinutes,
      dailyCap: link.dailyCap,
      startIncrementMinutes: link.startIncrementMinutes,
    },
  }
}

/**
 * How many of the nine editable values differ from the baseline.
 *
 * Strict equality throughout, and that is load-bearing on the limits: `null`
 * inherits the global and `0` is a deliberate zero, so any comparison written
 * with `||` or `??` would call those two equal and leave Save disabled on a
 * change the operator can see on screen.
 *
 * The durations count as one field however many of them moved, and order counts
 * — it is the order of the booking page's tabs, not an ascending list.
 */
export function changedCount(baseline: EditorDraft, draft: EditorDraft): number {
  let changed = CLEAN

  if (baseline.name !== draft.name) changed += ONE_FIELD
  if (baseline.slug !== draft.slug) changed += ONE_FIELD
  if (baseline.description !== draft.description) changed += ONE_FIELD
  if (baseline.patternId !== draft.patternId) changed += ONE_FIELD

  const sameDurations =
    baseline.durations.length === draft.durations.length &&
    baseline.durations.every((minutes, index) => minutes === draft.durations[index])
  if (!sameDurations) changed += ONE_FIELD

  for (const key of LIMIT_KEYS) {
    if (baseline.limits[key] !== draft.limits[key]) changed += ONE_FIELD
  }

  return changed
}

/** `2 unsaved changes`, and the singular, because `1 changes` is a bug people notice. */
export function unsavedLabel(changed: number): string {
  if (changed === CLEAN) return 'No unsaved changes'
  return changed === ONE_FIELD ? '1 unsaved change' : `${changed} unsaved changes`
}
