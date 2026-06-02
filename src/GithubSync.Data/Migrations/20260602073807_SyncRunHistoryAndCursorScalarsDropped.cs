using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncRunHistoryAndCursorScalarsDropped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRunCompletedAt",
                table: "SyncCursors");

            migrationBuilder.DropColumn(
                name: "LastRunMessage",
                table: "SyncCursors");

            migrationBuilder.DropColumn(
                name: "LastRunStartedAt",
                table: "SyncCursors");

            migrationBuilder.DropColumn(
                name: "LastRunStatus",
                table: "SyncCursors");

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IssuesCommitted = table.Column<int>(type: "integer", nullable: false),
                    EventsAttempted = table.Column<int>(type: "integer", nullable: false),
                    EventsInserted = table.Column<int>(type: "integer", nullable: false),
                    EventsSkippedUnknownKind = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_SyncConfigurations_SyncConfigurationId",
                        column: x => x.SyncConfigurationId,
                        principalTable: "SyncConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_SyncConfigurationId_StartedAt",
                table: "SyncRuns",
                columns: new[] { "SyncConfigurationId", "StartedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRunCompletedAt",
                table: "SyncCursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRunMessage",
                table: "SyncCursors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRunStartedAt",
                table: "SyncCursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastRunStatus",
                table: "SyncCursors",
                type: "integer",
                nullable: true);
        }
    }
}
