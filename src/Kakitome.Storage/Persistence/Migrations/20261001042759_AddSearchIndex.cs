using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kakitome.Storage.Persistence.Migrations
{
    /// <summary>FTS5 full-text index (trigram tokenizer for Japanese substring search). Derived data; rebuildable.</summary>
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE IF NOT EXISTS SearchSegments USING fts5(" +
                "recording_id UNINDEXED, kind UNINDEXED, segment_id UNINDEXED, start_seconds UNINDEXED, text, " +
                "tokenize = 'trigram case_sensitive 0');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS SearchSegments;");
        }
    }
}
