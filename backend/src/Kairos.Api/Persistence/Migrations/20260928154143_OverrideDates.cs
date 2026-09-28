using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class OverrideDates : Migration
{
    /// <summary>
    /// Written by hand. The scaffold drops the two minute columns, which on SQLite is a table rebuild
    /// that EF runs outside the migration's transaction under PRAGMA foreign_keys = 0, and it builds
    /// the unique date index before a split day's rows are folded, so it fails on any database
    /// holding one. Renaming the old table aside keeps the whole change in one transaction.
    /// </summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date_StartMinute_EndMinute",
            table: "PatternOverrides");

        migrationBuilder.RenameTable(
            name: "PatternOverrides",
            newName: "LegacyPatternOverrides");

        migrationBuilder.CreateTable(
            name: "PatternOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PatternId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PatternOverrides", x => x.Id);
                table.ForeignKey(
                    name: "FK_PatternOverrides_Patterns_PatternId",
                    column: x => x.PatternId,
                    principalTable: "Patterns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OverrideWindows",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PatternOverrideId = table.Column<Guid>(type: "TEXT", nullable: false),
                StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
                EndMinute = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OverrideWindows", x => x.Id);
                table.ForeignKey(
                    name: "FK_OverrideWindows_PatternOverrides_PatternOverrideId",
                    column: x => x.PatternOverrideId,
                    principalTable: "PatternOverrides",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // One date row per date, keeping the lowest id as OverrideWindowsUnique did. A window keeps
        // its old row's id. A row missing either minute copies as no window at all: that is how
        // v0.1.0 read it, whether it was the blocked marker or half a window no writer produced.
        migrationBuilder.Sql(
            """
            INSERT INTO PatternOverrides (Id, PatternId, Date)
            SELECT MIN(Id), PatternId, Date
            FROM LegacyPatternOverrides
            GROUP BY PatternId, Date;

            INSERT INTO OverrideWindows (Id, PatternOverrideId, StartMinute, EndMinute)
            SELECT legacy.Id, parent.Id, legacy.StartMinute, legacy.EndMinute
            FROM LegacyPatternOverrides AS legacy
            INNER JOIN PatternOverrides AS parent
                ON parent.PatternId = legacy.PatternId AND parent.Date = legacy.Date
            WHERE legacy.StartMinute IS NOT NULL AND legacy.EndMinute IS NOT NULL;
            """);

        migrationBuilder.DropTable(name: "LegacyPatternOverrides");

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OverrideWindows_PatternOverrideId_StartMinute_EndMinute",
            table: "OverrideWindows",
            columns: new[] { "PatternOverrideId", "StartMinute", "EndMinute" },
            unique: true);
    }

    /// <summary>
    /// The same shape in reverse, in one transaction for the same reason. A date with no windows
    /// goes back as the null row; a null row that sat beside windows before the upgrade stays gone,
    /// which v0.1.0 reads identically.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_OverrideWindows_PatternOverrideId_StartMinute_EndMinute",
            table: "OverrideWindows");

        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides");

        migrationBuilder.CreateTable(
            name: "LegacyPatternOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PatternId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                StartMinute = table.Column<int>(type: "INTEGER", nullable: true),
                EndMinute = table.Column<int>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PatternOverrides", x => x.Id);
                table.ForeignKey(
                    name: "FK_PatternOverrides_Patterns_PatternId",
                    column: x => x.PatternId,
                    principalTable: "Patterns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql(
            """
            INSERT INTO LegacyPatternOverrides (Id, PatternId, Date, StartMinute, EndMinute)
            SELECT window.Id, parent.PatternId, parent.Date, window.StartMinute, window.EndMinute
            FROM OverrideWindows AS window
            INNER JOIN PatternOverrides AS parent ON parent.Id = window.PatternOverrideId;

            INSERT INTO LegacyPatternOverrides (Id, PatternId, Date, StartMinute, EndMinute)
            SELECT parent.Id, parent.PatternId, parent.Date, NULL, NULL
            FROM PatternOverrides AS parent
            WHERE NOT EXISTS (
                SELECT 1 FROM OverrideWindows AS window WHERE window.PatternOverrideId = parent.Id);
            """);

        migrationBuilder.DropTable(name: "OverrideWindows");

        migrationBuilder.DropTable(name: "PatternOverrides");

        migrationBuilder.RenameTable(
            name: "LegacyPatternOverrides",
            newName: "PatternOverrides");

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date_StartMinute_EndMinute",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date", "StartMinute", "EndMinute" },
            unique: true);
    }
}
