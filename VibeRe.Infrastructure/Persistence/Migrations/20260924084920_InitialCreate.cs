using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeRe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "context_blobs",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_context_blobs", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "context_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayloadHash = table.Column<string>(type: "TEXT", nullable: false),
                    PrefixHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_context_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_context_nodes_context_blobs_PayloadHash",
                        column: x => x.PayloadHash,
                        principalTable: "context_blobs",
                        principalColumn: "Hash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_context_nodes_context_nodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "context_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_context_nodes_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "context_branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    HeadNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ForkedFromNodeId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_context_branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_context_branches_context_nodes_ForkedFromNodeId",
                        column: x => x.ForkedFromNodeId,
                        principalTable: "context_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_context_branches_context_nodes_HeadNodeId",
                        column: x => x.HeadNodeId,
                        principalTable: "context_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_context_branches_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StepId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_events_context_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "context_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_conversation_events_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_context_branches_ConversationId_Name",
                table: "context_branches",
                columns: new[] { "ConversationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_context_branches_ForkedFromNodeId",
                table: "context_branches",
                column: "ForkedFromNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_context_branches_HeadNodeId",
                table: "context_branches",
                column: "HeadNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_context_nodes_ConversationId_TurnId",
                table: "context_nodes",
                columns: new[] { "ConversationId", "TurnId" });

            migrationBuilder.CreateIndex(
                name: "IX_context_nodes_ParentId",
                table: "context_nodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_context_nodes_PayloadHash",
                table: "context_nodes",
                column: "PayloadHash");

            migrationBuilder.CreateIndex(
                name: "IX_context_nodes_PrefixHash",
                table: "context_nodes",
                column: "PrefixHash");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_events_ConversationId_Sequence",
                table: "conversation_events",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_events_ConversationId_TurnId_Sequence",
                table: "conversation_events",
                columns: new[] { "ConversationId", "TurnId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_events_NodeId",
                table: "conversation_events",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_Status_CreatedAt",
                table: "conversations",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "context_branches");

            migrationBuilder.DropTable(
                name: "conversation_events");

            migrationBuilder.DropTable(
                name: "context_nodes");

            migrationBuilder.DropTable(
                name: "context_blobs");

            migrationBuilder.DropTable(
                name: "conversations");
        }
    }
}
