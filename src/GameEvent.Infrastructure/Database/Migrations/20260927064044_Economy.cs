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
            migrationBuilder.DropIndex(
                name: "IX_SeasonPlayer_NextTimerAt",
                table: "SeasonPlayer");

            migrationBuilder.DropColumn(
                name: "EconomyJson",
                table: "SeasonPlayer");

            migrationBuilder.DropColumn(
                name: "NextTimerAt",
                table: "SeasonPlayer");

            migrationBuilder.DropColumn(
                name: "DiceModsJson",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "ObjectId",
                table: "PendingManualEffect");
        }
    }
}
