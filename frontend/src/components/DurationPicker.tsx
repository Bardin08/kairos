/**
 * How long the invitee needs. Present only when the link offers more than one
 * length; a single-duration link states it as text, because a control with one
 * option is furniture. The branch is here rather than at the call site so no
 * caller can forget it. Durations render in the link's own order — sorting them
 * would overrule whoever arranged them.
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
