using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class PatternConcurrencyToken : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Version",
            table: "Patterns",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Version",
            table: "Patterns");
    }
}
