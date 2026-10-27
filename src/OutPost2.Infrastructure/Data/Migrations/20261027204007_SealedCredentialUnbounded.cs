using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutPost2.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SealedCredentialUnbounded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename + widen, NOT drop + add. EF scaffolded the destructive form because it
            // cannot infer a rename; run as generated it would silently discard every
            // stored credential, forcing all users to re-authorise every connection.
            migrationBuilder.RenameColumn(
                name: "CredentialRef",
                table: "connections",
                newName: "SealedCredential");

            // varchar(200) never fit the sealed blob: a YouTrack PAT seals to a few hundred
            // bytes and an MSAL token cache to kilobytes.
            migrationBuilder.AlterColumn<string>(
                name: "SealedCredential",
                table: "connections",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing back will fail loudly on any row longer than 200 chars, which is
            // the correct outcome: a silent truncation here would corrupt credentials
            // beyond recovery.
            migrationBuilder.AlterColumn<string>(
                name: "SealedCredential",
                table: "connections",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.RenameColumn(
                name: "SealedCredential",
                table: "connections",
                newName: "CredentialRef");
        }
    }
}
