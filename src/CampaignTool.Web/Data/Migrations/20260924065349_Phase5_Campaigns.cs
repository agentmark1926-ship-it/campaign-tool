using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampaignTool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase5_Campaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Campaigns",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Html");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastBatchAtUtc",
                table: "Campaigns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "Campaigns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "LastBatchAtUtc",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "Campaigns");
        }
    }
}
