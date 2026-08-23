using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class SplitPatternVersion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Written by hand, and the direction matters. Version was raised by override saves and by
        // nothing else, so it is OverridesVersion that inherits it — EF scaffolded the rename onto
        // RulesVersion instead, which would have handed the week a number it never earned and
        // reset every live pattern's override guard to zero. A drop-and-add would be worse still:
        // two writers who had both read a pattern before the deployment would come back to a
        // column that matches them both.
        migrationBuilder.RenameColumn(
            name: "Version",
            table: "Patterns",
            newName: "OverridesVersion");

        migrationBuilder.AddColumn<int>(
            name: "RulesVersion",
            table: "Patterns",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RulesVersion",
            table: "Patterns");

        migrationBuilder.RenameColumn(
            name: "OverridesVersion",
            table: "Patterns",
            newName: "Version");
    }
}
