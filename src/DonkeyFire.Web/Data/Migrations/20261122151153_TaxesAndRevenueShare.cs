using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DonkeyFire.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaxesAndRevenueShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ForMonth",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromRevenueShare",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OutsideCityLimits",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RevenueShareCogsBasis",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RevenueSharePartner",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RevenueSharePercent",
                table: "Businesses",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Percent = table.Column<decimal>(type: "TEXT", nullable: false),
                    FiledWith = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxRates_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_BusinessId",
                table: "TaxRates",
                column: "BusinessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxRates");

            migrationBuilder.DropColumn(
                name: "ForMonth",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "ExcludeFromRevenueShare",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OutsideCityLimits",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RevenueShareCogsBasis",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "RevenueSharePartner",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "RevenueSharePercent",
                table: "Businesses");
        }
    }
}
