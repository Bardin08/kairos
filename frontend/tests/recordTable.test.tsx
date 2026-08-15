import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import RecordTable, { type RecordColumn } from '../src/components/RecordTable'

type Row = { slug: string; margin: string }

const columns: RecordColumn<Row>[] = [
  { label: 'Link', kind: 'identity', cell: (row) => row.slug },
  { label: 'Margin', kind: 'numeric', cell: (row) => row.margin },
  { label: 'Active', kind: 'control', cell: () => <button type="button">Pause</button> },
]

const rows: Row[] = [
  { slug: 'mentorship', margin: '15 min' },
  { slug: 'consulting', margin: '30 min' },
]

function cells(container: HTMLElement) {
  return [...container.querySelectorAll('tbody tr:first-child td')].map((cell) => ({
    column: cell.getAttribute('data-column'),
    numeric: cell.classList.contains('numeric'),
  }))
}

afterEach(cleanup)

test('every cell keeps its column name, and the identity cell keeps none', () => {
  const { container } = render(
    <RecordTable columns={columns} rows={rows} rowKey={(row) => row.slug} empty={null} />,
  )

  // Below 760px the heading row is display:none and each cell reprints this
  // attribute in front of its value. A missing one is invisible on a laptop and
  // an orphaned number on a phone, which is the whole failure this asserts
  // against. The identity cell carries "" on purpose: it is the record's title,
  // and kairos.css has a selector for exactly that.
  expect(cells(container)).toEqual([
    { column: '', numeric: false },
    { column: 'Margin', numeric: true },
    { column: 'Active', numeric: false },
  ])
})

test('a control column keeps its heading for a screen reader and draws none', () => {
  render(<RecordTable columns={columns} rows={rows} rowKey={(row) => row.slug} empty={null} />)

  const heading = screen.getByRole('columnheader', { name: 'Active' })

  expect(heading.querySelector('.visually-hidden')).not.toBeNull()
})

test('no rows renders the caller’s empty state instead of a table', () => {
  const { container } = render(
    <RecordTable
      columns={columns}
      rows={[]}
      rowKey={(row) => row.slug}
      empty={<p className="empty">No links yet.</p>}
    />,
  )

  // A rowless table is not an empty-looking table on a phone, it is a blank
  // screen: the heading row that would have said what is missing is hidden at
  // that width.
  expect(container.querySelector('table')).toBeNull()
  expect(screen.getByText('No links yet.')).toBeDefined()
})
