import { expect, test } from 'vitest'

import { monthCells } from '../src/lib/time'

const CELLS_PER_ROW = 7
const ROWS = 6
const CELLS = CELLS_PER_ROW * ROWS

// Monday-first (see time.ts's SUNDAY_TO_MONDAY_FIRST). 2026-02-01 is a Sunday,
// so February leads with six blanks and totals 34 cells — five rows.
// 2026-08-01 is a Saturday, leading with five blanks and totals 36 cells —
// six rows. The pair is chosen because one is short and the other is the
// longest a month gets, not because of anything else about them.
test('every month is the same six rows, so the calendar never changes height', () => {
  // A five-row month and a six-row month must produce the same number of
  // cells, or the card moves when the invitee changes month.
  expect(monthCells('2026-02-01')).toHaveLength(CELLS)
  expect(monthCells('2026-08-01')).toHaveLength(CELLS)
})

test('the padding is empty cells, and the real days are untouched', () => {
  const cells = monthCells('2026-02-01')
  expect(cells.filter((cell) => cell !== null)).toHaveLength(28)
  expect(cells.at(-1)).toBeNull()
})
