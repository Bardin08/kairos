using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairos.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class PersonalInvites : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PersonalInvites",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                Prefill = table.Column<string>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true),
                RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                BookingId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonalInvites", x => x.Id);
                table.ForeignKey(
                    name: "FK_PersonalInvites_Links_LinkId",
                    column: x => x.LinkId,
                    principalTable: "Links",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PersonalInvites_LinkId_CreatedAt",
            table: "PersonalInvites",
            columns: new[] { "LinkId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_PersonalInvites_TokenHash",
            table: "PersonalInvites",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PersonalInvites");
    }
}
