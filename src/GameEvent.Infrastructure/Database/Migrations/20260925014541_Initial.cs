using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Category",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Game",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Game", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommandType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CommandHash = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UndoneByEventId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Season",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Deadline = table.Column<long>(type: "INTEGER", nullable: true),
                    RulesetVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesetJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Season", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Login = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedLogin = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ruleset",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ruleset", x => new { x.SeasonId, x.Version });
                    table.ForeignKey(
                        name: "FK_Ruleset_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeasonPlayer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CellId = table.Column<string>(type: "TEXT", nullable: false),
                    Points = table.Column<int>(type: "INTEGER", nullable: false),
                    Coins = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsInactive = table.Column<bool>(type: "INTEGER", nullable: false),
                    PathJson = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OfferJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChoiceJson = table.Column<string>(type: "TEXT", nullable: true),
                    RerollsThisRoll = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveRunId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonPlayer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonPlayer_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerGameExclusion",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerGameExclusion", x => new { x.PlayerId, x.GameId });
                    table.ForeignKey(
                        name: "FK_PlayerGameExclusion_SeasonPlayer_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "SeasonPlayer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Run",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    RolledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Difficulty = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Hours = table.Column<decimal>(type: "TEXT", nullable: true),
                    DiceJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengeDiceJson = table.Column<string>(type: "TEXT", nullable: false),
                    HoursSource = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReachedFinish = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Run", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Run_SeasonPlayer_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "SeasonPlayer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Run_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingManualEffect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DrawEvent = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingManualEffect", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingManualEffect_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingManualEffect_SeasonPlayer_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "SeasonPlayer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingManualEffect_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Proof",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LinksJson = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    WitnessId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proof", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_Proof_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Proof_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Review",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Review", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_Review_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Review_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameEvent_CommandId",
                table: "GameEvent",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_GameEvent_SeasonId_Sequence",
                table: "GameEvent",
                columns: new[] { "SeasonId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingManualEffect_PlayerId",
                table: "PendingManualEffect",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingManualEffect_RunId",
                table: "PendingManualEffect",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingManualEffect_SeasonId_PlayerId",
                table: "PendingManualEffect",
                columns: new[] { "SeasonId", "PlayerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Proof_SeasonId_Status",
                table: "Proof",
                columns: new[] { "SeasonId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Review_GameId",
                table: "Review",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_Review_PlayerId",
                table: "Review",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Review_SeasonId",
                table: "Review",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_PlayerId",
                table: "Run",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_SeasonId_GameId",
                table: "Run",
                columns: new[] { "SeasonId", "GameId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonPlayer_SeasonId_Points",
                table: "SeasonPlayer",
                columns: new[] { "SeasonId", "Points" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonPlayer_SeasonId_UserId",
                table: "SeasonPlayer",
                columns: new[] { "SeasonId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_NormalizedLogin",
                table: "User",
                column: "NormalizedLogin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Category");

            migrationBuilder.DropTable(
                name: "Game");

            migrationBuilder.DropTable(
                name: "GameEvent");

            migrationBuilder.DropTable(
                name: "PendingManualEffect");

            migrationBuilder.DropTable(
                name: "PlayerGameExclusion");

            migrationBuilder.DropTable(
                name: "Proof");

            migrationBuilder.DropTable(
                name: "Review");

            migrationBuilder.DropTable(
                name: "Ruleset");

            migrationBuilder.DropTable(
                name: "User");

            migrationBuilder.DropTable(
                name: "Run");

            migrationBuilder.DropTable(
                name: "SeasonPlayer");

            migrationBuilder.DropTable(
                name: "Season");
        }
    }
}
