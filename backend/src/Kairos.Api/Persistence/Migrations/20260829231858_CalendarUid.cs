using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class CalendarUid : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CalendarUid",
            table: "CalendarEntries",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CalendarUid",
            table: "CalendarEntries");
    }
}
