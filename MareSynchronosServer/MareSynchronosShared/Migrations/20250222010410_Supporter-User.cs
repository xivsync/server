using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class SupporterUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "user_uid",
                table: "supports",
                type: "character varying(10)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_supports_user_uid",
                table: "supports",
                column: "user_uid");

            migrationBuilder.AddForeignKey(
                name: "fk_supports_users_user_uid",
                table: "supports",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_supports_users_user_uid",
                table: "supports");

            migrationBuilder.DropIndex(
                name: "ix_supports_user_uid",
                table: "supports");

            migrationBuilder.DropColumn(
                name: "user_uid",
                table: "supports");
        }
    }
}
