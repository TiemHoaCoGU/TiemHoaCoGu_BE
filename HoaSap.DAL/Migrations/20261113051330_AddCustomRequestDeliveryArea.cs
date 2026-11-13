using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaSap.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomRequestDeliveryArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryProvinceId",
                table: "CustomOrderRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryProvinceName",
                table: "CustomOrderRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryWardId",
                table: "CustomOrderRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryWardName",
                table: "CustomOrderRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryProvinceId",
                table: "CustomOrderRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryProvinceName",
                table: "CustomOrderRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryWardId",
                table: "CustomOrderRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryWardName",
                table: "CustomOrderRequests");
        }
    }
}
