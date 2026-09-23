using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampaignTool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_Imports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Delimiter",
                table: "Imports",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HasHeader",
                table: "Imports",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MappingJson",
                table: "Imports",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Imports_Status",
                table: "Imports",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Imports_Status",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "Delimiter",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "HasHeader",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "MappingJson",
                table: "Imports");
        }
    }
}
