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
            // SQLite drops a column in place (3.35+): a rebuilt table would change the text of the schema the rollback check
            // compares with the previous release (J5, as the Economy migration does).
            migrationBuilder.Sql("ALTER TABLE \"Run\" DROP COLUMN \"DeclaredDifficulty\";");
        }
    }
}
