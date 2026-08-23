using System;

using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class Proposals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ProposalHoldHours",
            table: "Settings",
            type: "INTEGER",
            nullable: false,
            defaultValue: SchedulingDefaults.ProposalHoldHours);

        migrationBuilder.AddColumn<string>(
            name: "CancellationReason",
            table: "Bookings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "BookingProposals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                NotifiedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookingProposals", x => x.Id);
                table.ForeignKey(
                    name: "FK_BookingProposals_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.UpdateData(
            table: "Settings",
            keyColumn: "Id",
            keyValue: 1,
            column: "ProposalHoldHours",
            value: 48);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Settings_ProposalHoldHours_AtLeastOne",
            table: "Settings",
            sql: "ProposalHoldHours >= 1");

        migrationBuilder.CreateIndex(
            name: "IX_BookingProposals_BookingId",
            table: "BookingProposals",
            column: "BookingId",
            unique: true,
            filter: "State = 10");

        migrationBuilder.CreateIndex(
            name: "IX_BookingProposals_State_ExpiresAt",
            table: "BookingProposals",
            columns: new[] { "State", "ExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_BookingProposals_TokenHash",
            table: "BookingProposals",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BookingProposals");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Settings_ProposalHoldHours_AtLeastOne",
            table: "Settings");

        migrationBuilder.DropColumn(
            name: "ProposalHoldHours",
            table: "Settings");

        migrationBuilder.DropColumn(
            name: "CancellationReason",
            table: "Bookings");
    }
}
