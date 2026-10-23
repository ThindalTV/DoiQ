using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoiQ.Core.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_Status_DoneAt",
                table: "Tasks",
                columns: new[] { "BoardId", "Status", "DoneAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_Status_DoneAt",
                table: "Tasks");
        }
    }
}
