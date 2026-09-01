using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class Reminders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Reminders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Reminders", x => x.Id);
                table.ForeignKey(
                    name: "FK_Reminders_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Reminders_BookingId",
            table: "Reminders",
            column: "BookingId");

        migrationBuilder.CreateIndex(
            name: "IX_Reminders_SentAt_DueAt",
            table: "Reminders",
            columns: new[] { "SentAt", "DueAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Reminders");
    }
}
