using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class FeedReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedReaction",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetCommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedReaction", x => new { x.SeasonId, x.TargetCommandId, x.UserId, x.Emoji });
                    table.ForeignKey(
                        name: "FK_FeedReaction_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedReaction");
        }
    }
}
