/**
 * The mark beside a message that names a problem.
 *
 * It exists as its own file because DESIGN.md's Second Channel Rule puts it
 * next to every error the product shows, so it was byte-identical in three
 * places. Shared rather than copied a fourth time.
 */
export default function AlertIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7.5v5M12 16h.01" />
    </svg>
  )
}
