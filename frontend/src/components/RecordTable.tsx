import type { ReactNode } from 'react'

/**
 * One column: its heading, and how a row's cell is drawn.
 *
 * `label` does double duty. Above 760px it is the column heading; below it
 * kairos.css reprints it before the value via
 * `td::before { content: attr(data-column) }` — a bare "15 min" in a stack of
 * records means nothing.
 *
 * `kind` covers the three cells that are not plain text:
 * - `identity` — names the row. Its heading shows, but the record repeats no
 *   label: "LINK Mentorship" reads as a form field, not a heading.
 * - `numeric` — a time, date, duration or count. Tabular figures, which
 *   DESIGN.md requires of all four.
 * - `control` — a switch or button. Its heading is for a screen reader only.
 */
export type RecordColumn<Row> = {
  label: string
  cell: (row: Row) => ReactNode
  kind?: 'identity' | 'numeric' | 'control'
}

/**
 * A table that becomes a list of records on a phone.
 *
 * The responsive half is kairos.css's — below 760px the heading row is hidden
 * and every cell reprints its own column name. All this emits is the markup
 * those rules read: `.table-wrap > .table`, a `<th scope="col">` per column, and
 * `data-column` on every `<td>`. A cell that loses it looks fine on a laptop and
 * is an orphaned value on a phone, which is why the tests assert the attribute.
 *
 * `empty` is required: below 760px a rowless table draws literally nothing, and
 * only the caller knows the reason. "No links yet" and "No meetings match this
 * filter" are different sentences.
 */
export default function RecordTable<Row>({
  columns,
  rows,
  rowKey,
  empty,
}: {
  columns: RecordColumn<Row>[]
  rows: Row[]
  /** A stable identifier per row. Not the index: filtering reorders these. */
  rowKey: (row: Row) => string
  empty: ReactNode
}) {
  if (rows.length === 0) return <>{empty}</>

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.label} scope="col">
                {column.kind === 'control' ? (
                  <span className="visually-hidden">{column.label}</span>
                ) : (
                  column.label
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((column) => (
                <td
                  key={column.label}
                  // Empty on the identity column, and empty rather than absent:
                  // `td[data-column=""]` is the selector that lays it out as the
                  // record's title instead of as a labelled row.
                  data-column={column.kind === 'identity' ? '' : column.label}
                  className={column.kind === 'numeric' ? 'numeric' : undefined}
                >
                  {column.cell(row)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
