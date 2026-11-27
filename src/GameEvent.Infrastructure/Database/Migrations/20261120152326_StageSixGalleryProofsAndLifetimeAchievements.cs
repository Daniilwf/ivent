using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class StageSixGalleryProofsAndLifetimeAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConsentRevoked",
                table: "GalleryMoment",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ProofApprovalCommandId",
                table: "GalleryMoment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProofFileId",
                table: "GalleryMoment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProofRunId",
                table: "GalleryMoment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountAchievement",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    EarnedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAchievement", x => new { x.UserId, x.ObjectId });
                    table.ForeignKey(
                        name: "FK_AccountAchievement_SeasonPlayer_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "SeasonPlayer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountAchievement_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountAchievement_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAchievement_PlayerId",
                table: "AccountAchievement",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAchievement_SeasonId",
                table: "AccountAchievement",
                column: "SeasonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAchievement");

            migrationBuilder.DropColumn(
                name: "ConsentRevoked",
                table: "GalleryMoment");

            migrationBuilder.DropColumn(
                name: "ProofApprovalCommandId",
                table: "GalleryMoment");

            migrationBuilder.DropColumn(
                name: "ProofFileId",
                table: "GalleryMoment");

            migrationBuilder.DropColumn(
                name: "ProofRunId",
                table: "GalleryMoment");
        }
    }
}
