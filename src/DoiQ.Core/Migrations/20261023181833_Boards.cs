using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoiQ.Core.Migrations
{
    /// <inheritdoc />
    public partial class Boards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default board exists before the BoardId columns do, so every existing row (and a fresh database) lands on it.
            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false, collation: "NOCASE"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Boards",
                columns: new[] { "Id", "Name", "CreatedAt", "Version" },
                values: new object[] { 1, "Main", DateTime.UtcNow, 1L });

            migrationBuilder.DropIndex(
                name: "IX_Tasks_SourceRef",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Status_SortOrder",
                table: "Tasks");

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "Outbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_SourceRef",
                table: "Tasks",
                columns: new[] { "BoardId", "SourceRef" },
                unique: true,
                filter: "SourceRef IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_Status_SortOrder",
                table: "Tasks",
                columns: new[] { "BoardId", "Status", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_BoardId",
                table: "Outbox",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_BoardId",
                table: "Events",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Boards_Name",
                table: "Boards",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Boards_BoardId",
                table: "Events",
                column: "BoardId",
                principalTable: "Boards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Outbox_Boards_BoardId",
                table: "Outbox",
                column: "BoardId",
                principalTable: "Boards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Boards_BoardId",
                table: "Tasks",
                column: "BoardId",
                principalTable: "Boards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Boards_BoardId",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_Outbox_Boards_BoardId",
                table: "Outbox");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Boards_BoardId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_SourceRef",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_Status_SortOrder",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Outbox_BoardId",
                table: "Outbox");

            migrationBuilder.DropIndex(
                name: "IX_Events_BoardId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SourceRef",
                table: "Tasks",
                column: "SourceRef",
                unique: true,
                filter: "SourceRef IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Status_SortOrder",
                table: "Tasks",
                columns: new[] { "Status", "SortOrder" });
        }
    }
}
