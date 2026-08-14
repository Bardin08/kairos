using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class Integrations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CalendarEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<int>(type: "INTEGER", nullable: false),
                ExternalEventId = table.Column<string>(type: "TEXT", nullable: true),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalendarEntries", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalendarEntries_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ConferenceLinks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<int>(type: "INTEGER", nullable: false),
                ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                JoinUrl = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConferenceLinks", x => x.Id);
                table.ForeignKey(
                    name: "FK_ConferenceLinks_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "IntegrationCredentials",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<int>(type: "INTEGER", nullable: false),
                ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                AccountLabel = table.Column<string>(type: "TEXT", nullable: true),
                ConnectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationCredentials", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                DispatchedAt = table.Column<long>(type: "INTEGER", nullable: true),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_OutboxMessages_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CalendarEntries_BookingId_Provider",
            table: "CalendarEntries",
            columns: new[] { "BookingId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ConferenceLinks_BookingId_Provider",
            table: "ConferenceLinks",
            columns: new[] { "BookingId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_IntegrationCredentials_Provider",
            table: "IntegrationCredentials",
            column: "Provider",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_BookingId_Type",
            table: "OutboxMessages",
            columns: new[] { "BookingId", "Type" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_DispatchedAt",
            table: "OutboxMessages",
            column: "DispatchedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CalendarEntries");

        migrationBuilder.DropTable(
            name: "ConferenceLinks");

        migrationBuilder.DropTable(
            name: "IntegrationCredentials");

        migrationBuilder.DropTable(
            name: "OutboxMessages");
    }
}
