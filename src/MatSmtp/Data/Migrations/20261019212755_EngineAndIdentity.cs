using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatSmtp.Data.Migrations
{
    /// <inheritdoc />
    public partial class EngineAndIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdToken",
                table: "UserSession",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "User",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiId",
                table: "RelayUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManagedBy",
                table: "RelayUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalSubject",
                table: "User",
                column: "ExternalSubject",
                unique: true,
                filter: "\"ExternalSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RelayUser_ApiId",
                table: "RelayUser",
                column: "ApiId",
                unique: true,
                filter: "\"UpdateState\" <> 0 AND \"ApiId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_ExternalSubject",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_RelayUser_ApiId",
                table: "RelayUser");

            migrationBuilder.DropColumn(
                name: "IdToken",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ApiId",
                table: "RelayUser");

            migrationBuilder.DropColumn(
                name: "ManagedBy",
                table: "RelayUser");
        }
    }
}
