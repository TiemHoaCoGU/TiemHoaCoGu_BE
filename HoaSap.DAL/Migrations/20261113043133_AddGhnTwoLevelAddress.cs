using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaSap.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddGhnTwoLevelAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProvinceId",
                table: "ShippingQuotes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProvinceName",
                table: "ShippingQuotes",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WardId",
                table: "ShippingQuotes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WardName",
                table: "ShippingQuotes",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShippingProvinceId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingProvinceName",
                table: "Orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShippingWardId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingWardName",
                table: "Orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvinceId",
                table: "ShippingQuotes");

            migrationBuilder.DropColumn(
                name: "ProvinceName",
                table: "ShippingQuotes");

            migrationBuilder.DropColumn(
                name: "WardId",
                table: "ShippingQuotes");

            migrationBuilder.DropColumn(
                name: "WardName",
                table: "ShippingQuotes");

            migrationBuilder.DropColumn(
                name: "ShippingProvinceId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingProvinceName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingWardId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingWardName",
                table: "Orders");
        }
    }
}
