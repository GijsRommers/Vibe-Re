using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeRe.Server.Infrastructure.Persistence.Migrations;

public partial class AddDeviceSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DeviceSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Permissions = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_DeviceSessions", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_DeviceSessions_TokenHash",
            table: "DeviceSessions",
            column: "TokenHash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "DeviceSessions");
}
