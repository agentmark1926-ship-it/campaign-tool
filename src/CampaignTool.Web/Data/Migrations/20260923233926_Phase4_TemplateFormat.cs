using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampaignTool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4_TemplateFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Templates",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Html"); // existing templates are HTML

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "Templates",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "Templates");
        }
    }
}
