using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class nullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chat_log_groups_group_id",
                table: "chat_log");

            migrationBuilder.DropForeignKey(
                name: "fk_chat_log_users_sender_id",
                table: "chat_log");

            migrationBuilder.DropForeignKey(
                name: "fk_moodles_users_user_uid",
                table: "moodles");

            migrationBuilder.DropForeignKey(
                name: "fk_pfinder_groups_group_id",
                table: "pfinder");

            migrationBuilder.AddForeignKey(
                name: "fk_chat_log_groups_group_id",
                table: "chat_log",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_chat_log_users_sender_id",
                table: "chat_log",
                column: "sender_id",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_moodles_users_user_uid",
                table: "moodles",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_pfinder_groups_group_id",
                table: "pfinder",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chat_log_groups_group_id",
                table: "chat_log");

            migrationBuilder.DropForeignKey(
                name: "fk_chat_log_users_sender_id",
                table: "chat_log");

            migrationBuilder.DropForeignKey(
                name: "fk_moodles_users_user_uid",
                table: "moodles");

            migrationBuilder.DropForeignKey(
                name: "fk_pfinder_groups_group_id",
                table: "pfinder");

            migrationBuilder.AddForeignKey(
                name: "fk_chat_log_groups_group_id",
                table: "chat_log",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "gid");

            migrationBuilder.AddForeignKey(
                name: "fk_chat_log_users_sender_id",
                table: "chat_log",
                column: "sender_id",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_moodles_users_user_uid",
                table: "moodles",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_pfinder_groups_group_id",
                table: "pfinder",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "gid");
        }
    }
}
