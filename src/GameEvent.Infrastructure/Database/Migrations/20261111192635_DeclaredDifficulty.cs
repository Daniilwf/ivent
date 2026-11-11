using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DeclaredDifficulty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeclaredDifficulty",
                table: "Run",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclaredDifficulty",
                table: "Run");
        }
    }
}
