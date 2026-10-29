using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoiQ.Core.Migrations
{
    /// <inheritdoc />
    public partial class Orchestrators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Actors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOrchestrator",
                table: "Actors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Actors");

            migrationBuilder.DropColumn(
                name: "IsOrchestrator",
                table: "Actors");
        }
    }
}
