using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalActors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceActorId = table.Column<string>(type: "text", nullable: false),
                    SourceActorLogin = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalActors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceLocator = table.Column<string>(type: "jsonb", nullable: false),
                    TargetSystem = table.Column<int>(type: "integer", nullable: false),
                    TargetLocator = table.Column<string>(type: "jsonb", nullable: false),
                    TargetTypeMapping = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSystem = table.Column<int>(type: "integer", nullable: false),
                    TargetUserId = table.Column<string>(type: "text", nullable: false),
                    TargetUserDisplayName = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSystem = table.Column<int>(type: "integer", nullable: false),
                    TargetUserId = table.Column<string>(type: "text", nullable: false),
                    TargetUserDisplayName = table.Column<string>(type: "text", nullable: false),
                    MappingSource = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityMappings_CanonicalActors_CanonicalActorId",
                        column: x => x.CanonicalActorId,
                        principalTable: "CanonicalActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CanonicalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceEntityType = table.Column<int>(type: "integer", nullable: false),
                    SourceEntityId = table.Column<string>(type: "text", nullable: false),
                    SourceEventId = table.Column<string>(type: "text", nullable: true),
                    EventKind = table.Column<int>(type: "integer", nullable: false),
                    EventTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanonicalEvents_CanonicalActors_ActorId",
                        column: x => x.ActorId,
                        principalTable: "CanonicalActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CanonicalEvents_SyncConfigurations_SyncConfigurationId",
                        column: x => x.SyncConfigurationId,
                        principalTable: "SyncConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastEventTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastETag = table.Column<string>(type: "text", nullable: true),
                    LastRunStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<int>(type: "integer", nullable: true),
                    LastRunMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncCursors_SyncConfigurations_SyncConfigurationId",
                        column: x => x.SyncConfigurationId,
                        principalTable: "SyncConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceEntityType = table.Column<int>(type: "integer", nullable: false),
                    SourceEntityId = table.Column<string>(type: "text", nullable: false),
                    TargetSystem = table.Column<int>(type: "integer", nullable: false),
                    TargetEntityId = table.Column<string>(type: "text", nullable: false),
                    TargetWorkItemType = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemMappings_SyncConfigurations_SyncConfigurationId",
                        column: x => x.SyncConfigurationId,
                        principalTable: "SyncConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSystem = table.Column<int>(type: "integer", nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RawResponse = table.Column<string>(type: "jsonb", nullable: true),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadLetters_CanonicalEvents_CanonicalEventId",
                        column: x => x.CanonicalEventId,
                        principalTable: "CanonicalEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalActors_Source_SourceActorId",
                table: "CanonicalActors",
                columns: new[] { "Source", "SourceActorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalEvents_ActorId",
                table: "CanonicalEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalEvents_Source_SourceEntityType_SourceEntityId_Even~",
                table: "CanonicalEvents",
                columns: new[] { "Source", "SourceEntityType", "SourceEntityId", "EventKind", "EventTime", "SourceEventId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalEvents_SyncConfigurationId",
                table: "CanonicalEvents",
                column: "SyncConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_CanonicalEventId_Resolved",
                table: "DeadLetters",
                columns: new[] { "CanonicalEventId", "Resolved" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMappings_CanonicalActorId_TargetSystem",
                table: "IdentityMappings",
                columns: new[] { "CanonicalActorId", "TargetSystem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConfigurations_Source_SourceLocator_TargetSystem_Target~",
                table: "SyncConfigurations",
                columns: new[] { "Source", "SourceLocator", "TargetSystem", "TargetLocator" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncCursors_SyncConfigurationId",
                table: "SyncCursors",
                column: "SyncConfigurationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetUsers_TargetSystem_TargetUserId",
                table: "TargetUsers",
                columns: new[] { "TargetSystem", "TargetUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemMappings_SyncConfigurationId_Source_SourceEntityTyp~",
                table: "WorkItemMappings",
                columns: new[] { "SyncConfigurationId", "Source", "SourceEntityType", "SourceEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemMappings_SyncConfigurationId_TargetSystem_TargetEnt~",
                table: "WorkItemMappings",
                columns: new[] { "SyncConfigurationId", "TargetSystem", "TargetEntityId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadLetters");

            migrationBuilder.DropTable(
                name: "IdentityMappings");

            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.DropTable(
                name: "TargetUsers");

            migrationBuilder.DropTable(
                name: "WorkItemMappings");

            migrationBuilder.DropTable(
                name: "CanonicalEvents");

            migrationBuilder.DropTable(
                name: "CanonicalActors");

            migrationBuilder.DropTable(
                name: "SyncConfigurations");
        }
    }
}
