using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class SettingsStartIncrementFloor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Settings_StartIncrementMinutes_NonNegative",
            table: "Settings");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Settings_StartIncrementMinutes_AtLeastOne",
            table: "Settings",
            sql: "StartIncrementMinutes >= 1");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Settings_StartIncrementMinutes_AtLeastOne",
            table: "Settings");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Settings_StartIncrementMinutes_NonNegative",
            table: "Settings",
            sql: "StartIncrementMinutes >= 0");
    }
}
