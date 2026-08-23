import type { AdminLink, LinkLimits } from './adminLinks'
import { LIMIT_KEYS } from './limitCopy'

/**
 * The editor's working copy of everything the form can change: the five fields
 * `adminLinks`' `LinkDraft` also carries, plus the four limits it does not. That
 * other `LinkDraft` is a request body; this one is form state, wider by four
 * fields and never sent as-is. `active` has its own route and belongs to neither.
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

/**
 * What a new link starts as. One duration, because zero is not a link. 30 is not
 * pinned to anything: `DurationEditor.tsx`'s `PRESETS` owns "the four lengths
 * nearly every link uses", and nothing keeps this value inside that set. Drop 30
 * from `PRESETS` and this default sits here unchanged, offering new links a
 * length the picker no longer has.
 */
const NEW_DURATION_MINUTES = 30
const NEW_DURATIONS = [NEW_DURATION_MINUTES]

/**
 * Nothing has moved. Named because it is a state, not an arithmetic zero, and
 * exported so a caller writes `changedCount(...) !== CLEAN` rather than a bare
 * `0`.
 */
export const CLEAN = 0
const ONE_FIELD = 1

/** The four scalar fields — everything in `EditorDraft` that isn't compared structurally. */
type ScalarKey = keyof Omit<EditorDraft, 'durations' | 'limits'>

/**
 * Drives the scalar half of `changedCount`'s loop. Before this, a field added to
 * `EditorDraft` could silently go uncounted — `draftOf` would fail to compile on
 * both of its return literals, but `changedCount` would compile clean and
 * undercount.
 */
const SCALAR_KEYS = ['name', 'slug', 'description', 'patternId'] as const satisfies readonly ScalarKey[]

/**
 * `satisfies` only checks one direction — every listed key is a real
 * `ScalarKey`, not that every `ScalarKey` is listed. This is the other
 * direction: a scalar field left out of `SCALAR_KEYS` fails here instead of
 * silently undercounting.
 */
type EveryScalarKeyListed = [ScalarKey] extends [(typeof SCALAR_KEYS)[number]] ? true : never
const _everyScalarKeyListed: EveryScalarKeyListed = true
void _everyScalarKeyListed

/**
 * The link as a draft, or the shape a new one starts in. The limits are copied
 * field by field rather than by spreading the link, so a null here is this
 * module's own null and never a value some later edit resolved.
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
 * Strict equality throughout, load-bearing on the limits: `null` inherits the
 * global and `0` is a deliberate zero, so a comparison written with `||` or `??`
 * would call those two equal and leave Save disabled on a visible change.
 *
 * The durations count as one field however many of them moved, and order counts
 * — it is the order of the booking page's tabs, not an ascending list.
 */
export function changedCount(baseline: EditorDraft, draft: EditorDraft): number {
  let changed = CLEAN

  for (const key of SCALAR_KEYS) {
    if (baseline[key] !== draft[key]) changed += ONE_FIELD
  }

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
