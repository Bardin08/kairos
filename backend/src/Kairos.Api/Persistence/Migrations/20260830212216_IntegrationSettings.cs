using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class IntegrationSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Enabled",
            table: "IntegrationCredentials",
            type: "INTEGER",
            nullable: false,
            // The generator wrote false here, from the CLR default rather than from the property
            // initialiser, and this line was changed by hand. Every credential already stored was
            // connected to be used, so an upgrade that landed the generated value would pause every
            // integration on the operator's behalf without saying so.
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "IntegrationSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<int>(type: "INTEGER", nullable: false),
                Values = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationSettings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IntegrationSettings_Provider",
            table: "IntegrationSettings",
            column: "Provider",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IntegrationSettings");

        migrationBuilder.DropColumn(
            name: "Enabled",
            table: "IntegrationCredentials");
    }
}
