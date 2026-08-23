import AlertIcon from '../../components/AlertIcon'

/**
 * The quiet state of the condition surface: a marker that something is still
 * wrong, in the rail on desktop and beside Settings on a phone.
 *
 * It says a count rather than a provider name — at two conditions a name would
 * have to pick one, and this control's only job is to get the operator back to
 * the sentence that explains it.
 *
 * A button and not a link: it expands the banner, it does not navigate. That is
 * also why it cannot reuse `.nav a`, whose every rule is written for anchors.
 */
export default function ConditionRow({
  count,
  controls,
  onExpand,
}: {
  count: number
  /** The banner region's id, so the marker and what it restores are tied together. */
  controls: string
  onExpand: () => void
}) {
  if (count === 0) return null

  return (
    <button
      type="button"
      className="condition-row"
      aria-expanded="false"
      aria-controls={controls}
      onClick={onExpand}
    >
      <AlertIcon />
      {count === 1 ? '1 problem' : `${count} problems`}
    </button>
  )
}
