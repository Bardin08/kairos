import type { ReactNode } from 'react'

/**
 * One column: its heading, and how a row's cell is drawn.
 *
 * `label` does double duty. Above 760px it is the column heading; below it
 * kairos.css reprints it in front of the value from
 * `td::before { content: attr(data-column) }`, because the heading row is gone
 * by then and a bare "15 min" in a stack of records means nothing.
 *
 * `kind` covers the three cells that are not plain text, and nothing else:
 *
 * - `identity` — the column that names the row. Its heading shows, but the
 *   record repeats no label in front of it: "LINK Mentorship" reads as a form
 *   field rather than as a heading.
 * - `numeric` — a time, a date, a duration or a count. Sets in tabular figures,
 *   which DESIGN.md requires of all four.
 * - `control` — a switch or a button. The heading is there for a screen reader
 *   and drawn nowhere, since a column of switches needs no title.
 */
export type RecordColumn<Row> = {
  label: string
  cell: (row: Row) => ReactNode
  kind?: 'identity' | 'numeric' | 'control'
}

/**
 * A table that becomes a list of records on a phone.
 *
 * The responsive half is kairos.css's, not this component's — below 760px the
 * heading row is hidden and every cell reprints its own column name. All this
 * has to do is emit the markup those rules read: `.table-wrap > .table`, a
 * `<th scope="col">` per column, and `data-column` on every single `<td>`. A
 * cell that loses its `data-column` does not look wrong on a laptop and is an
 * orphaned value on a phone, which is why the tests assert the attribute rather
 * than the appearance.
 *
 * `empty` is required: below 760px a rowless table draws literally nothing — no
 * heading row survives to say what is missing — and DESIGN.md asks an empty
 * state to name the reason, which only the caller knows. "No links yet" and "No
 * meetings match this filter" are different sentences.
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
