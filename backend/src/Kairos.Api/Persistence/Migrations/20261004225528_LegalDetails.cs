using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class LegalDetails : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ContactEmail",
            table: "Settings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Jurisdiction",
            table: "Settings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OperatorName",
            table: "Settings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.UpdateData(
            table: "Settings",
            keyColumn: "Id",
            keyValue: 1,
            columns: new[] { "ContactEmail", "Jurisdiction", "OperatorName" },
            values: new object[] { null, null, null });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ContactEmail",
            table: "Settings");

        migrationBuilder.DropColumn(
            name: "Jurisdiction",
            table: "Settings");

        migrationBuilder.DropColumn(
            name: "OperatorName",
            table: "Settings");
    }
}
