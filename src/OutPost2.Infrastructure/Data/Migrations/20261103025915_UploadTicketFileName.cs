using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutPost2.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UploadTicketFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "upload_tickets",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileName",
                table: "upload_tickets");
        }
    }
}
