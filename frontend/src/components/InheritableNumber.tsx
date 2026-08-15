import { useEffect, useId, useRef } from 'react'

/**
 * Three states through controls that natively offer two.
 *
 * A limit can be inherited, an explicit zero, or a value — and the whole
 * global-then-override design rests on the first two being different: `null`
 * inherits the global, `0` is a deliberate zero. A plain number input expresses
 * two of the three. It cannot hold "inherit" at all, and the obvious substitute
 * — an empty box — is indistinguishable from a box the operator has not filled
 * in yet, so the moment anything coerces it the link silently stops tracking
 * the global and nothing on screen says so.
 *
 * The first version of this component solved that with a checkbox under a
 * disabled input. It held all three states and read backwards: the control that
 * governed the field sat below it in the faintest type on the page, and the
 * inherited state was a box showing a number the operator had never typed and
 * could not edit.
 *
 * So the two states become two different things rather than one thing twice:
 *
 * - inherited  → the global, stated as read-only prose, with a Global chip and
 *                an Override button. There is no input at all.
 * - overridden → a number field the width of its longest value, with the unit
 *                inside it and a button back to the global.
 *
 * Nothing here is carried by colour. The states differ in shape and in the word
 * `Global`, so the control survives a monochrome screenshot — DESIGN.md's
 * Second Channel Rule.
 */
export default function InheritableNumber({
  label,
  short,
  help,
  value,
  globalValue,
  min,
  onChange,
}: {
  label: string
  /** The unit at affix width — "min", "a day". Shown, never spoken. */
  short: string
  /** One line under the label. Omitted on screens that carry it elsewhere. */
  help?: string
  /** `null` inherits the global. A number, `0` included, is this link's own. */
  value: number | null
  /** The global this inherits when `value` is null. Stated, not editable. */
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

  const inputRef = useRef<HTMLInputElement>(null)
  const overrideRef = useRef<HTMLButtonElement>(null)
  // Which control the operator should land on after the swap.
  const follow = useRef<'input' | 'override' | null>(null)

  useEffect(() => {
    if (follow.current === 'input') inputRef.current?.focus()
    if (follow.current === 'override') overrideRef.current?.focus()
    follow.current = null
  }, [inherited])

  return (
    <div className="field-row">
      <div className="field-row-label">
        {/* A label needs a control. While inherited there is none, so the name
            is a span and the value beside it is plain text. */}
        {inherited ? (
          <span className="field-row-name">{label}</span>
        ) : (
          <label htmlFor={id}>{label}</label>
        )}
        {help !== undefined && <p className="fine">{help}</p>}
      </div>

      <div className="field-row-control">
        {inherited ? (
          <>
            <span className="numeric">
              {globalValue} <span className="unit-inline">{short}</span>
            </span>
            <span className="badge">Global</span>
            <button
              ref={overrideRef}
              className="btn btn-ghost"
              type="button"
              // Named in full, because `Override` four times in one group is
              // unusable by voice control and by a screen reader's element list.
              aria-label={`Override ${label}`}
              onClick={() => {
                follow.current = 'input'
                // Seeded with the global rather than an empty box: it is the
                // value the link is on right now. Clamped because the global and
                // this link's floor are set by two different endpoints and can
                // disagree, and the PUT carrying an under-floor value would come
                // back as an opaque 400 about a number nobody typed.
                onChange(Math.max(min, globalValue))
              }}
            >
              Override
            </button>
          </>
        ) : (
          <>
            <span className="affix">
              <input
                ref={inputRef}
                id={id}
                className="input numeric ctl-num"
                type="number"
                inputMode="numeric"
                min={min}
                step={1}
                value={value}
                onChange={(event) => onChange(floor(event.target.value, min))}
              />
              {/* Shown, not spoken — the label already carries the unit, and
                  hearing "minutes" twice is worse than not seeing it once. */}
              <span className="affix-unit" aria-hidden="true">
                {short}
              </span>
            </span>
            <button
              className="btn btn-ghost"
              type="button"
              aria-label={`Use global ${label}`}
              onClick={() => {
                follow.current = 'override'
                onChange(null)
              }}
            >
              Use global
            </button>
          </>
        )}
      </div>
    </div>
  )
}

/** The base a whole number of minutes is parsed in. */
const DECIMAL = 10

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
  const parsed = Number.parseInt(raw, DECIMAL)
  return Number.isNaN(parsed) ? min : Math.max(min, parsed)
}
