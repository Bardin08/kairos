# Invariants

Not rules — decisions. Each one looks wrong until explained, and each has been broken by somebody reading only the code. Read before touching `Scheduling/` or the availability write path. The reasoning that produced them is the operator's, and is not needed here to apply them.

**A margin resolves to the maximum of the two adjacent margins, never the sum.** Consulting declares 30 minutes, mentorship 15, and a consulting call is followed by a mentorship call: the gap required is 30. Padding both sides instead gives the sum — pad the existing booking by its own 30 and the candidate by its own 15, and a candidate starting 40 minutes after that booking ends is rejected, because the required gap silently became 45. A margin belongs to the boundary between two meetings, not to one meeting.

**`null` and `0` are different values**, on all four resolved settings: margin, minimum notice, daily cap, start increment. `null` means inherit the global; `0` means deliberately none on this link. Store the override nullable and resolve at read time. Writing the global's value into the link row as a default means changing the global later silently fails to propagate.

**Slot generation is interval arithmetic, not a slot loop.** A booking occupies its duration plus the resolved margin, and the next legal start is the following start-increment boundary after that — not the moment the previous meeting ends. Notice, daily cap and start increment are parameters of the arithmetic rather than special cases layered on top.

**Intervals are half-open**: `[start, end)`. A meeting ending at 10:00 and one starting at 10:00 do not overlap.

**One writer.** The write lock exists because a booking that passes the conflict check and a booking that passes it a millisecond later must not both be accepted.

**Freebusy goes in and is never read back.** External calendars are read as opaque busy intervals — no title, no attendees, no description — and every calendar the operator grants is read, because more calendars is strictly better for correctness. Meetings never displays an event Kairos did not create. That opacity is what makes reading widely safe, and it does not bend when a provider offers a richer query.

**A duration is any whole number of minutes.** 25 and 50 are ordinary. No surface hard-codes a duration or assumes a round one, and an end time frequently lands off the half hour.
