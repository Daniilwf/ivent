using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameEvent.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class GalleryMoments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GalleryMediaBinding",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryMediaBinding", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_GalleryMediaBinding_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GalleryMediaBinding_StoredFile_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GalleryMoment",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedDay = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryMoment", x => new { x.SeasonId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_GalleryMoment_Season_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMediaBinding_SeasonId",
                table: "GalleryMediaBinding",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMoment_SeasonId_Hidden_CreatedAt_ItemId",
                table: "GalleryMoment",
                columns: new[] { "SeasonId", "Hidden", "CreatedAt", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMoment_SeasonId_PlayerId_Hidden_CreatedAt_ItemId",
                table: "GalleryMoment",
                columns: new[] { "SeasonId", "PlayerId", "Hidden", "CreatedAt", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GalleryMediaBinding");

            migrationBuilder.DropTable(
                name: "GalleryMoment");
        }
    }
}
