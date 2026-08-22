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

        // Before the index, or the index cannot be created on a database that already holds a
        // pair. The lowest id wins in each group, which is the earliest row by Uuid7 ordering —
        // an arbitrary choice among rows that are identical in every column that matters.
        migrationBuilder.Sql(
            """
            DELETE FROM PatternOverrides
            WHERE Id NOT IN (
                SELECT MIN(Id) FROM PatternOverrides
                GROUP BY PatternId, Date, IFNULL(StartMinute, -1), IFNULL(EndMinute, -1)
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
