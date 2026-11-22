using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DonkeyFire.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SquareSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Fee",
                table: "Sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Sales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Tip",
                table: "Sales",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SquareNames",
                table: "MenuItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SquareLastSyncAt",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareLocationId",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareLocationName",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SquareSyncFrom",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareTimeZone",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_BusinessId_ExternalId",
                table: "Sales",
                columns: new[] { "BusinessId", "ExternalId" },
                unique: true,
                filter: "ExternalId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_BusinessId_ExternalId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Fee",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Tip",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "SquareNames",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "SquareLastSyncAt",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SquareLocationId",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SquareLocationName",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SquareSyncFrom",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SquareTimeZone",
                table: "Businesses");
        }
    }
}
