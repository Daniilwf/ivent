using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class GameDeletionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Game",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite drops the column in place: EF's DropColumn rebuilds the table with its columns reordered, and the
            // rollback must give the previous release's schema back exactly (npm run test:migrations)
            migrationBuilder.Sql("ALTER TABLE \"Game\" DROP COLUMN \"DeletionReason\";");
        }
    }
}
