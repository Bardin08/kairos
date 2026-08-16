using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class OverrideWindows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides");

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides");

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date" },
            unique: true);
    }
}
