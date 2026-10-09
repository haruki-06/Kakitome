using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kakitome.Storage.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLibraryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Artifacts",
                columns: table => new
                {
                    RecordingId = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => new { x.RecordingId, x.FileName });
                });

            migrationBuilder.CreateTable(
                name: "LibraryIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recordings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Project = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    SourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    CaptureStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    HasAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasTranscript = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasSummary = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasExternalChanges = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recordings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordingTags",
                columns: table => new
                {
                    RecordingId = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingTags", x => new { x.RecordingId, x.Tag });
                    table.ForeignKey(
                        name: "FK_RecordingTags_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_CreatedAtUtcTicks",
                table: "Recordings",
                column: "CreatedAtUtcTicks");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_Folder",
                table: "Recordings",
                column: "Folder",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_Project",
                table: "Recordings",
                column: "Project");

            migrationBuilder.CreateIndex(
                name: "IX_RecordingTags_Tag",
                table: "RecordingTags",
                column: "Tag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Artifacts");

            migrationBuilder.DropTable(
                name: "LibraryIssues");

            migrationBuilder.DropTable(
                name: "RecordingTags");

            migrationBuilder.DropTable(
                name: "Recordings");
        }
    }
}
