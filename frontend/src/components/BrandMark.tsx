/**
 * The Kairos mark at header size: the hinted four-cell drawing, three days and
 * the chosen one in the top-right cell. docs/ui/DESIGN.md has its construction;
 * its colours come from kairos.css so the dark theme redraws it.
 */
export default function BrandMark() {
  return (
    <span className="wordmark-glyph" aria-hidden="true">
      <svg viewBox="0 0 32 32">
        <rect className="mark-tile" width="32" height="32" rx="7" />
        <circle className="mark-day" cx="10" cy="10" r="2" />
        <circle className="mark-day" cx="10" cy="22" r="2" />
        <circle className="mark-day" cx="22" cy="22" r="2" />
        <circle className="mark-chosen" cx="22" cy="10" r="6" />
      </svg>
    </span>
  )
}
