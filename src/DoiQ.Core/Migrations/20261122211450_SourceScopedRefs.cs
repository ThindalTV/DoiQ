using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoiQ.Core.Migrations
{
    /// <inheritdoc />
    public partial class SourceScopedRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_SourceRef",
                table: "Tasks");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_Source_SourceRef",
                table: "Tasks",
                columns: new[] { "BoardId", "Source", "SourceRef" },
                unique: true,
                filter: "SourceRef IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_Source_SourceRef",
                table: "Tasks");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_SourceRef",
                table: "Tasks",
                columns: new[] { "BoardId", "SourceRef" },
                unique: true,
                filter: "SourceRef IS NOT NULL");
        }
    }
}
