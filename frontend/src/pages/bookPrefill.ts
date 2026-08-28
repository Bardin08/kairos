/** Everything the booking form asks the invitee for. */
export type Details = { name: string; email: string; notes: string }

/**
 * The fields the form has an input for, which is what makes a prefill readable
 * at all: the server carries a stored key it no longer knows through rather than
 * refusing the invite, so the page is the side that ignores one.
 */
export const FILLABLE = ['name', 'email', 'notes'] as const

/**
 * The invite's values over the fields the invitee has not been in.
 *
 * `touched` is the whole point. The invite read is a round trip and the form is
 * already on screen, so it can resolve after the invitee has started typing —
 * and an operator's guess landing on top of what somebody typed is worse than no
 * prefill at all. Touched rather than non-empty: an invitee who cleared a field
 * meant to clear it.
 */
export function applyPrefill(
  current: Details,
  prefill: Record<string, string>,
  touched: ReadonlySet<string>,
): Details {
  const filled = { ...current }

  for (const field of FILLABLE) {
    if (touched.has(field)) continue
    const value = prefill[field]
    if (value !== undefined) filled[field] = value
  }

  return filled
}
