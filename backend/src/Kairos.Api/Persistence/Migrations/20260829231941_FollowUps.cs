using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class FollowUps : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FollowUps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Secret = table.Column<string>(type: "TEXT", nullable: true),
                PreviousStartUtc = table.Column<long>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FollowUps", x => x.Id);
                table.ForeignKey(
                    name: "FK_FollowUps_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FollowUps_Attempts_CreatedAt_Id",
            table: "FollowUps",
            columns: new[] { "Attempts", "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_FollowUps_BookingId",
            table: "FollowUps",
            column: "BookingId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FollowUps");
    }
}
