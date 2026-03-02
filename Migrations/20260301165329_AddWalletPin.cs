using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace momo_wallet.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Pin",
                table: "Wallets",
                type: "character varying(4)",
                maxLength: 4,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Pin",
                table: "Wallets");
        }
    }
}
