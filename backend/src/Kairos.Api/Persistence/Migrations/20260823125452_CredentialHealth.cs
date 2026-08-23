using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class CredentialHealth : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Health",
            table: "IntegrationCredentials",
            type: "INTEGER",
            nullable: false,
            // 10 is CredentialHealth.Healthy. The generator's 0 is not a value of that enum,
            // and a credential holding it would render as neither healthy nor broken — on a
            // screen whose whole job is telling those two apart.
            defaultValue: 10);

        migrationBuilder.AddColumn<string>(
            name: "LastError",
            table: "IntegrationCredentials",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastSuccessAt",
            table: "IntegrationCredentials",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Health",
            table: "IntegrationCredentials");

        migrationBuilder.DropColumn(
            name: "LastError",
            table: "IntegrationCredentials");

        migrationBuilder.DropColumn(
            name: "LastSuccessAt",
            table: "IntegrationCredentials");
    }
}
