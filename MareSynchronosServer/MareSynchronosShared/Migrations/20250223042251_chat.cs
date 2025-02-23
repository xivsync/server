using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    time_stamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    sender_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    group_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_log_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "gid");
                    table.ForeignKey(
                        name: "fk_chat_log_users_sender_id",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_log_group_id",
                table: "chat_log",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_log_sender_id",
                table: "chat_log",
                column: "sender_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_log");
        }
    }
}
