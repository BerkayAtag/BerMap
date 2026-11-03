using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BerMap.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3DiscoveryAndSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Collectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Hostname = table.Column<string>(type: "text", nullable: false),
                    IPAddress = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupportedAdaptersJson = table.Column<string>(type: "text", nullable: false),
                    QueueMetricsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collectors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InfrastructureNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FQDN = table.Column<string>(type: "text", nullable: false),
                    IPAddress = table.Column<string>(type: "text", nullable: true),
                    MacAddress = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DiscoverySource = table.Column<string>(type: "text", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    BiosUuid = table.Column<string>(type: "text", nullable: false),
                    VmInstanceUuid = table.Column<string>(type: "text", nullable: false),
                    DomainSid = table.Column<string>(type: "text", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    StaleSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InfrastructureNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    AdapterType = table.Column<string>(type: "text", nullable: false),
                    AdapterVersion = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    CredentialReference = table.Column<string>(type: "text", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTestSuccess = table.Column<bool>(type: "boolean", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: false),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DiscoveredEntitiesCount = table.Column<int>(type: "integer", nullable: false),
                    ScheduleCron = table.Column<string>(type: "text", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiscoveryJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CandidateNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceSystem = table.Column<string>(type: "text", nullable: false),
                    MatchKey = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    MatchScore = table.Column<double>(type: "double precision", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    DiscoveredNodeJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateNodeIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateSummary = table.Column<string>(type: "text", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "text", nullable: false),
                    ResolutionNotes = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    RoleType = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Simulations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioType = table.Column<string>(type: "text", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Simulations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    RelationshipCount = table.Column<int>(type: "integer", nullable: false),
                    IsImmutable = table.Column<bool>(type: "boolean", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    GraphCapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InfrastructureRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<int>(type: "integer", nullable: false),
                    ConfidenceLevel = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    SourceSystem = table.Column<string>(type: "text", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LastSeenJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    StaleSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InfrastructureRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InfrastructureRelationships_InfrastructureNodes_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "InfrastructureNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InfrastructureRelationships_InfrastructureNodes_TargetNodeId",
                        column: x => x.TargetNodeId,
                        principalTable: "InfrastructureNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EntitiesDiscovered = table.Column<int>(type: "integer", nullable: false),
                    RelationshipsDiscovered = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    CurrentPhase = table.Column<string>(type: "text", nullable: false),
                    CurrentMessage = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ErrorsJson = table.Column<string>(type: "jsonb", nullable: false),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    StatisticsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryJobs_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeSourceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    SourceSystem = table.Column<string>(type: "text", nullable: false),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    LastSeenJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeSourceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeSourceLinks_InfrastructureNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "InfrastructureNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NodeSourceLinks_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FQDN = table.Column<string>(type: "text", nullable: false),
                    IPAddress = table.Column<string>(type: "text", nullable: true),
                    MacAddress = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DiscoverySource = table.Column<string>(type: "text", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    BiosUuid = table.Column<string>(type: "text", nullable: false),
                    VmInstanceUuid = table.Column<string>(type: "text", nullable: false),
                    DomainSid = table.Column<string>(type: "text", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GraphVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapshotNodes_Snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<int>(type: "integer", nullable: false),
                    ConfidenceLevel = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    SourceSystem = table.Column<string>(type: "text", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GraphVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapshotRelationships_Snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                table: "AuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_IntegrationId_StartedAt",
                table: "DiscoveryJobs",
                columns: new[] { "IntegrationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_Status",
                table: "DiscoveryJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_BiosUuid",
                table: "InfrastructureNodes",
                column: "BiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_DomainSid",
                table: "InfrastructureNodes",
                column: "DomainSid");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_FQDN",
                table: "InfrastructureNodes",
                column: "FQDN");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_IsStale",
                table: "InfrastructureNodes",
                column: "IsStale");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_LastSeen",
                table: "InfrastructureNodes",
                column: "LastSeen");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_MacAddress",
                table: "InfrastructureNodes",
                column: "MacAddress");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_NodeType",
                table: "InfrastructureNodes",
                column: "NodeType");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_SnapshotVersion",
                table: "InfrastructureNodes",
                column: "SnapshotVersion");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureNodes_VmInstanceUuid",
                table: "InfrastructureNodes",
                column: "VmInstanceUuid");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_IntegrationId",
                table: "InfrastructureRelationships",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_IsStale",
                table: "InfrastructureRelationships",
                column: "IsStale");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_SnapshotVersion",
                table: "InfrastructureRelationships",
                column: "SnapshotVersion");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_SourceNodeId_RelationshipType",
                table: "InfrastructureRelationships",
                columns: new[] { "SourceNodeId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_SourceNodeId_TargetNodeId_Relat~",
                table: "InfrastructureRelationships",
                columns: new[] { "SourceNodeId", "TargetNodeId", "RelationshipType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_SourceSystem",
                table: "InfrastructureRelationships",
                column: "SourceSystem");

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRelationships_TargetNodeId_RelationshipType",
                table: "InfrastructureRelationships",
                columns: new[] { "TargetNodeId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_AdapterType",
                table: "Integrations",
                column: "AdapterType");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_IsEnabled_DeletedAt",
                table: "Integrations",
                columns: new[] { "IsEnabled", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_Status",
                table: "Integrations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NodeSourceLinks_IntegrationId_ExternalId",
                table: "NodeSourceLinks",
                columns: new[] { "IntegrationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeSourceLinks_IsStale",
                table: "NodeSourceLinks",
                column: "IsStale");

            migrationBuilder.CreateIndex(
                name: "IX_NodeSourceLinks_NodeId_SourceSystem",
                table: "NodeSourceLinks",
                columns: new[] { "NodeId", "SourceSystem" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReviews_CreatedAt",
                table: "ReconciliationReviews",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReviews_IntegrationId_Status",
                table: "ReconciliationReviews",
                columns: new[] { "IntegrationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReviews_Signature",
                table: "ReconciliationReviews",
                column: "Signature");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReviews_Status",
                table: "ReconciliationReviews",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotNodes_SnapshotId_BiosUuid",
                table: "SnapshotNodes",
                columns: new[] { "SnapshotId", "BiosUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotNodes_SnapshotId_FQDN",
                table: "SnapshotNodes",
                columns: new[] { "SnapshotId", "FQDN" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotNodes_SnapshotId_NodeId",
                table: "SnapshotNodes",
                columns: new[] { "SnapshotId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRelationships_SnapshotId_RelationshipId",
                table: "SnapshotRelationships",
                columns: new[] { "SnapshotId", "RelationshipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRelationships_SnapshotId_SourceNodeId_RelationshipT~",
                table: "SnapshotRelationships",
                columns: new[] { "SnapshotId", "SourceNodeId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRelationships_SourceSystem",
                table: "SnapshotRelationships",
                column: "SourceSystem");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_CreatedAt",
                table: "Snapshots",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_SnapshotVersion",
                table: "Snapshots",
                column: "SnapshotVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Collectors");

            migrationBuilder.DropTable(
                name: "DiscoveryJobs");

            migrationBuilder.DropTable(
                name: "InfrastructureRelationships");

            migrationBuilder.DropTable(
                name: "NodeSourceLinks");

            migrationBuilder.DropTable(
                name: "ReconciliationReviews");

            migrationBuilder.DropTable(
                name: "Simulations");

            migrationBuilder.DropTable(
                name: "SnapshotNodes");

            migrationBuilder.DropTable(
                name: "SnapshotRelationships");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "InfrastructureNodes");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropTable(
                name: "Snapshots");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
