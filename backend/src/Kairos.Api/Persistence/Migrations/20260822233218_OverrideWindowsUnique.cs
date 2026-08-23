using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class OverrideWindowsUnique : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides");

        // Two jobs, and the second is not an accident. It makes the index creatable — a database
        // already holding an identical pair would refuse CREATE UNIQUE INDEX — and it also folds
        // together the rows the index will not bound: SQLite treats nulls as distinct, so a date
        // blocked twice would survive the index, and a date blocked twice is nonsense whichever
        // way it got there. The lowest id wins in each group, the earliest row by Uuid7 ordering,
        // which is an arbitrary choice among rows identical in every column that matters.
        //
        // Grouped on "is it null" beside the value rather than on IFNULL(value, -1): -1 is a
        // number the column can hold, so that sentinel folded a real window in with the blocked
        // rows and deleted one of them. Only [Range] on the request keeps it out today, which is
        // not where a migration's correctness belongs.
        migrationBuilder.Sql(
            """
            DELETE FROM PatternOverrides
            WHERE Id NOT IN (
                SELECT MIN(Id) FROM PatternOverrides
                GROUP BY
                    PatternId,
                    Date,
                    StartMinute IS NULL, StartMinute,
                    EndMinute IS NULL, EndMinute
            );
            """);

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date_StartMinute_EndMinute",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date", "StartMinute", "EndMinute" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date_StartMinute_EndMinute",
            table: "PatternOverrides");

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date" });
    }
}
