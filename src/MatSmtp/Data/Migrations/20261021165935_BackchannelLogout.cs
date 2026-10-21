using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatSmtp.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackchannelLogout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentitySessionId",
                table: "UserSession",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_IdentitySessionId",
                table: "UserSession",
                column: "IdentitySessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSession_IdentitySessionId",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "IdentitySessionId",
                table: "UserSession");
        }
    }
}
