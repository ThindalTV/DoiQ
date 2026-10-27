using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoiQ.Core.Migrations
{
    /// <inheritdoc />
    public partial class DispatchAndMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgentMentionHops",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDispatcher",
                table: "Actors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Responsibilities",
                table: "Actors",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Mentions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: false),
                    MentionedId = table.Column<int>(type: "INTEGER", nullable: false),
                    ById = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Mentions_Actors_ById",
                        column: x => x.ById,
                        principalTable: "Actors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Mentions_Actors_MentionedId",
                        column: x => x.MentionedId,
                        principalTable: "Actors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Mentions_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Mentions_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mentions_ById",
                table: "Mentions",
                column: "ById");

            migrationBuilder.CreateIndex(
                name: "IX_Mentions_CommentId",
                table: "Mentions",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Mentions_MentionedId_AnsweredAt",
                table: "Mentions",
                columns: new[] { "MentionedId", "AnsweredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Mentions_TaskId",
                table: "Mentions",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Mentions");

            migrationBuilder.DropColumn(
                name: "AgentMentionHops",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IsDispatcher",
                table: "Actors");

            migrationBuilder.DropColumn(
                name: "Responsibilities",
                table: "Actors");
        }
    }
}
