using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DonkeyFire.Web.Data.Migrations
{
    /// <summary>
    /// The revenue share now counts only events at the partner's venues (opt in), instead of all events unless excluded.
    /// The old flag meant the opposite, so it is dropped rather than renamed.
    /// </summary>
    public partial class ParkVenueRevenueShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AtPartnerVenue",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.DropColumn(
                name: "ExcludeFromRevenueShare",
                table: "Events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromRevenueShare",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.DropColumn(
                name: "AtPartnerVenue",
                table: "Events");
        }
    }
}
