using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class Init : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Patterns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Patterns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                MarginMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                MinNoticeMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                DailyCap = table.Column<int>(type: "INTEGER", nullable: false),
                StartIncrementMinutes = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Settings", x => x.Id);
                table.CheckConstraint("CK_Settings_DailyCap_NonNegative", "DailyCap >= 0");
                table.CheckConstraint("CK_Settings_MarginMinutes_NonNegative", "MarginMinutes >= 0");
                table.CheckConstraint("CK_Settings_MinNoticeMinutes_NonNegative", "MinNoticeMinutes >= 0");
                table.CheckConstraint("CK_Settings_StartIncrementMinutes_NonNegative", "StartIncrementMinutes >= 0");
            });

        migrationBuilder.CreateTable(
            name: "Links",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Slug = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                PatternId = table.Column<Guid>(type: "TEXT", nullable: false),
                Active = table.Column<bool>(type: "INTEGER", nullable: false),
                MarginMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                MinNoticeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                DailyCap = table.Column<int>(type: "INTEGER", nullable: true),
                StartIncrementMinutes = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Links", x => x.Id);
                table.CheckConstraint("CK_Links_DailyCap_NonNegative", "DailyCap IS NULL OR DailyCap >= 0");
                table.CheckConstraint("CK_Links_MarginMinutes_NonNegative", "MarginMinutes IS NULL OR MarginMinutes >= 0");
                table.CheckConstraint("CK_Links_MinNoticeMinutes_NonNegative", "MinNoticeMinutes IS NULL OR MinNoticeMinutes >= 0");
                table.CheckConstraint("CK_Links_StartIncrementMinutes_NonNegative", "StartIncrementMinutes IS NULL OR StartIncrementMinutes >= 0");
                table.ForeignKey(
                    name: "FK_Links_Patterns_PatternId",
                    column: x => x.PatternId,
                    principalTable: "Patterns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PatternOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PatternId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                StartMinute = table.Column<int>(type: "INTEGER", nullable: true),
                EndMinute = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PatternOverrides", x => x.Id);
                table.ForeignKey(
                    name: "FK_PatternOverrides_Patterns_PatternId",
                    column: x => x.PatternId,
                    principalTable: "Patterns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PatternRules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PatternId = table.Column<Guid>(type: "TEXT", nullable: false),
                IsoDayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
                EndMinute = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PatternRules", x => x.Id);
                table.ForeignKey(
                    name: "FK_PatternRules_Patterns_PatternId",
                    column: x => x.PatternId,
                    principalTable: "Patterns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Bookings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ManageTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                CancelledAt = table.Column<long>(type: "INTEGER", nullable: true),
                SeriesId = table.Column<Guid>(type: "TEXT", nullable: true),
                RecurrenceRule = table.Column<string>(type: "TEXT", nullable: true),
                RecurrenceId = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bookings", x => x.Id);
                table.ForeignKey(
                    name: "FK_Bookings_Links_LinkId",
                    column: x => x.LinkId,
                    principalTable: "Links",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LinkDurations",
            columns: table => new
            {
                LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                Minutes = table.Column<int>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LinkDurations", x => new { x.LinkId, x.Minutes });
                table.ForeignKey(
                    name: "FK_LinkDurations_Links_LinkId",
                    column: x => x.LinkId,
                    principalTable: "Links",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "BookingParticipants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                TimeZoneId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookingParticipants", x => x.Id);
                table.ForeignKey(
                    name: "FK_BookingParticipants_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.InsertData(
            table: "Settings",
            columns: new[] { "Id", "DailyCap", "MarginMinutes", "MinNoticeMinutes", "StartIncrementMinutes", "TimeZoneId" },
            values: new object[] { 1, 4, 15, 120, 30, "UTC" });

        migrationBuilder.CreateIndex(
            name: "IX_BookingParticipants_BookingId",
            table: "BookingParticipants",
            column: "BookingId");

        migrationBuilder.CreateIndex(
            name: "IX_BookingParticipants_Email",
            table: "BookingParticipants",
            column: "Email");

        migrationBuilder.CreateIndex(
            name: "IX_Bookings_LinkId",
            table: "Bookings",
            column: "LinkId");

        migrationBuilder.CreateIndex(
            name: "IX_Bookings_ManageTokenHash",
            table: "Bookings",
            column: "ManageTokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Bookings_StartUtc",
            table: "Bookings",
            column: "StartUtc",
            unique: true,
            filter: "Status = 10");

        migrationBuilder.CreateIndex(
            name: "IX_Bookings_Status_StartUtc_EndUtc",
            table: "Bookings",
            columns: new[] { "Status", "StartUtc", "EndUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Links_PatternId",
            table: "Links",
            column: "PatternId");

        migrationBuilder.CreateIndex(
            name: "IX_Links_Slug",
            table: "Links",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PatternOverrides_PatternId_Date",
            table: "PatternOverrides",
            columns: new[] { "PatternId", "Date" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PatternRules_PatternId_IsoDayOfWeek",
            table: "PatternRules",
            columns: new[] { "PatternId", "IsoDayOfWeek" });

        migrationBuilder.CreateIndex(
            name: "IX_Patterns_Name",
            table: "Patterns",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BookingParticipants");

        migrationBuilder.DropTable(
            name: "LinkDurations");

        migrationBuilder.DropTable(
            name: "PatternOverrides");

        migrationBuilder.DropTable(
            name: "PatternRules");

        migrationBuilder.DropTable(
            name: "Settings");

        migrationBuilder.DropTable(
            name: "Bookings");

        migrationBuilder.DropTable(
            name: "Links");

        migrationBuilder.DropTable(
            name: "Patterns");
    }
}
