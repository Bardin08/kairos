/**
 * How long the invitee needs.
 *
 * Present only when the link offers more than one length; a single-duration
 * link states its duration as text, because a control with one option is
 * furniture. The branch lives here rather than at the call site so no caller
 * can forget it.
 *
 * The durations arrive in the link's own order and are rendered in it. Sorting
 * them here would quietly overrule whoever arranged them.
 */
export default function DurationPicker({
  durations,
  value,
  onChange,
}: {
  durations: number[]
  value: number
  onChange: (minutes: number) => void
}) {
  if (durations.length < 2) {
    return (
      <span className="numeric">
        <strong>{value} minutes</strong>
      </span>
    )
  }

  return (
    <div className="segmented" role="group" aria-label="How long do you need?">
      {durations.map((minutes) => (
        <button
          key={minutes}
          type="button"
          className="numeric"
          aria-pressed={minutes === value}
          onClick={() => onChange(minutes)}
        >
          {minutes} min
        </button>
      ))}
    </div>
  )
}
