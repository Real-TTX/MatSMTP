using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatSmtp.Data.Migrations
{
    /// <inheritdoc />
    public partial class DkimKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DkimKey",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Domain = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Selector = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    PrivateKeyProtected = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    KeyBits = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeSubdomains = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DnsState = table.Column<string>(type: "TEXT", nullable: false),
                    DnsCheckedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DnsDetail = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateState = table.Column<short>(type: "INTEGER", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkimKey", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DkimKey_Domain_Selector",
                table: "DkimKey",
                columns: new[] { "Domain", "Selector" },
                unique: true,
                filter: "\"UpdateState\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DkimKey");
        }
    }
}
