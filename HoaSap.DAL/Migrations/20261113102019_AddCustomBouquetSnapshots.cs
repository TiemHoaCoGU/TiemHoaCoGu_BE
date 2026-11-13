using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaSap.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomBouquetSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BouquetSnapshot",
                table: "CustomQuotationRevisions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BouquetSnapshot",
                table: "CustomOrderRequests",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BouquetSnapshot",
                table: "CustomQuotationRevisions");

            migrationBuilder.DropColumn(
                name: "BouquetSnapshot",
                table: "CustomOrderRequests");
        }
    }
}
