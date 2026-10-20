using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeRe.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentThreadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    AutoWake = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorktreePath = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceThreads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Handled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceThreadId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMessages_WorkspaceThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "WorkspaceThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMessages_ThreadId_Sequence",
                table: "WorkspaceMessages",
                columns: new[] { "ThreadId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceMessages");

            migrationBuilder.DropTable(
                name: "WorkspaceThreads");
        }
    }
}
