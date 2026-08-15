import { useId } from 'react'

/**
 * Three states through a control that natively offers two.
 *
 * A limit can be inherited, an explicit zero, or a value — and Part A built the
 * whole global-then-override design on the first two being different: `null`
 * inherits the global, `0` is a deliberate zero. A plain number input expresses
 * two of the three. It cannot hold "inherit" at all, and the obvious substitute
 * — an empty box — is indistinguishable from a box the operator has not filled
 * in yet, so the moment anything coerces it the link silently stops tracking
 * the global and nothing on screen says so.
 *
 * So the third state gets its own control. The checkbox owns "inherit" and the
 * number owns the value, and they are wired so that exactly one of them is
 * live at a time:
 *
 * - checked  → the number is disabled and `null` is what leaves this component
 * - unchecked → whatever the number reads leaves this component, `0` included
 *
 * While it is checked the box still SHOWS the global, greyed out by `disabled`,
 * rather than sitting empty. That is the difference between a control that
 * expresses three states and one that merely permits them: the operator reads
 * "15", sees it is not editable, and understands where the 15 came from.
 */
export default function InheritableNumber({
  label,
  unit,
  value,
  globalValue,
  min,
  onChange,
}: {
  label: string
  /** What the number means — "minutes", "meetings a day". Named in the checkbox's line. */
  unit: string
  /** `null` inherits the global. A number, `0` included, is this link's own. */
  value: number | null
  /** The global this inherits when `value` is null. Shown, disabled, while it does. */
  globalValue: number
  /**
   * The floor. Zero for margin, minimum notice and daily cap — no gap, book
   * immediately, nothing bookable today are all real settings. One for the
   * start increment, where zero is the value that makes the slot generator's
   * step never advance.
   */
  min: number
  onChange: (value: number | null) => void
}) {
  const id = useId()
  const inherited = value === null

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        className="input numeric"
        type="number"
        min={min}
        step={1}
        disabled={inherited}
        // The global while inherited, so the box is never empty and never lies.
        value={inherited ? globalValue : value}
        onChange={(event) => onChange(floor(event.target.value, min))}
      />
      {/* The third state, and the only control that can reach it. Unchecking
          seeds the number with the global rather than with an empty box: it is
          the value the link is on right now, so it is the honest starting
          point for editing it.

          Clamped on the way out, because the global and this link's floor are
          set by two different endpoints and can disagree. A global start
          increment below one would otherwise leave this box the moment the
          checkbox is cleared, and the PUT that carried it would come back as an
          opaque 400 about a value the operator never typed. */}
      <label className="fine row gap-2">
        <input
          type="checkbox"
          checked={inherited}
          onChange={(event) =>
            onChange(event.target.checked ? null : Math.max(min, globalValue))
          }
        />
        Use the global default — {globalValue} {unit}
      </label>
    </div>
  )
}

/**
 * A whole number at or above the floor, never NaN and never negative.
 *
 * `min` on the input is a hint a keyboard walks straight past: typing `-5` or
 * clearing the field both reach this handler, and a negative margin would
 * subtract from the busy interval and start creating overlaps — availability
 * that is not there. Parsed rather than read from `valueAsNumber` so the answer
 * does not depend on how faithfully the test environment implements it.
 */
function floor(raw: string, min: number): number {
  const parsed = Number.parseInt(raw, 10)
  return Number.isNaN(parsed) ? min : Math.max(min, parsed)
}
