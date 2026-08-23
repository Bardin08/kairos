import { useEffect, useId, useRef } from 'react'

/**
 * Three states through controls that natively offer two.
 *
 * `null` inherits the global, `0` is a deliberate zero, and the design rests on
 * those being different. A number input cannot hold "inherit", and an empty box
 * is indistinguishable from one not filled in yet, so anything that coerces it
 * silently stops the link tracking the global. The first attempt, a checkbox
 * under a disabled input, read backwards: the control governing the field sat
 * below it in the faintest type on the page.
 *
 * - inherited  → the global as read-only prose, a Global chip, an Override
 *                button. No input at all.
 * - overridden → a number field the width of its longest value, unit inside,
 *                and a button back to the global.
 *
 * Nothing is carried by colour: the states differ in shape and in the word
 * `Global` — DESIGN.md's Second Channel Rule.
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
   * immediately, nothing bookable today are all real settings. One for the start
   * increment, where zero makes the slot generator's step never advance.
   */
  min: number
  onChange: (value: number | null) => void
}) {
  const id = useId()
  const inherited = value === null

  const inputRef = useRef<HTMLInputElement>(null)
  const overrideRef = useRef<HTMLButtonElement>(null)
  // Which control the operator should land on after the swap. Set only inside
  // the two click handlers below and cleared once read, so a state change from
  // any other source — a Discard that resets every limit to null at once — moves
  // nothing. Without that guard, a bulk reset would remount every Override
  // button at once and focus would land on whichever won.
  const focusTarget = useRef<'input' | 'override' | null>(null)

  useEffect(() => {
    if (focusTarget.current === 'input') inputRef.current?.focus()
    if (focusTarget.current === 'override') overrideRef.current?.focus()
    focusTarget.current = null
  }, [inherited])

  // Read by the Override button's aria-describedby, so a screen-reader user who
  // navigates by interactive element — skipping the static prose — still hears
  // the global and the word "Global" before acting on the button.
  const valueId = `${id}-value`
  const badgeId = `${id}-badge`

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
            <span className="numeric" id={valueId}>
              {globalValue} <span className="unit-inline">{short}</span>
            </span>
            <span className="badge" id={badgeId}>
              Global
            </span>
            <button
              ref={overrideRef}
              className="btn btn-ghost"
              type="button"
              // Named in full, because `Override` four times in one group is
              // unusable by voice control and by a screen reader's element list.
              // The name must not grow the value, or it would change every time
              // the global does; the value travels through aria-describedby.
              aria-label={`Override ${label}`}
              aria-describedby={`${valueId} ${badgeId}`}
              onClick={() => {
                focusTarget.current = 'input'
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
                focusTarget.current = 'override'
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

const DECIMAL = 10

/**
 * A whole number at or above the floor, never NaN and never negative.
 *
 * `min` on the input is a hint a keyboard walks straight past: typing `-5` or
 * clearing the field both reach this handler, and a negative margin subtracts
 * from the busy interval and starts creating availability that is not there.
 * Parsed rather than read from `valueAsNumber`, which jsdom implements loosely.
 */
function floor(raw: string, min: number): number {
  const parsed = Number.parseInt(raw, DECIMAL)
  return Number.isNaN(parsed) ? min : Math.max(min, parsed)
}
