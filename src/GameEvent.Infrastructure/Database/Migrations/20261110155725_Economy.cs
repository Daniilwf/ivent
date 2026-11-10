using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Economy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EconomyJson",
                table: "SeasonPlayer",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextTimerAt",
                table: "SeasonPlayer",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiceModsJson",
                table: "Run",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectId",
                table: "PendingManualEffect",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeasonPlayer_NextTimerAt",
                table: "SeasonPlayer",
                column: "NextTimerAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite drops a column in place (3.35+): a rebuilt table would change the text of the schema the rollback
            // check compares with the previous release (J5), so the columns go one by one, the index first.
            migrationBuilder.DropIndex(
                name: "IX_SeasonPlayer_NextTimerAt",
                table: "SeasonPlayer");

            migrationBuilder.Sql("ALTER TABLE \"SeasonPlayer\" DROP COLUMN \"EconomyJson\";");
            migrationBuilder.Sql("ALTER TABLE \"SeasonPlayer\" DROP COLUMN \"NextTimerAt\";");
            migrationBuilder.Sql("ALTER TABLE \"Run\" DROP COLUMN \"DiceModsJson\";");
            migrationBuilder.Sql("ALTER TABLE \"PendingManualEffect\" DROP COLUMN \"ObjectId\";");
        }
    }
}
