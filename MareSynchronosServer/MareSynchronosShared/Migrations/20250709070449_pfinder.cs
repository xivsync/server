using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class pfinder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pfinder",
                columns: table => new
                {
                    guid = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_update = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    tags = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_nsfw = table.Column<bool>(type: "boolean", nullable: false),
                    open = table.Column<bool>(type: "boolean", nullable: false),
                    user_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    group_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pfinder", x => x.guid);
                    table.ForeignKey(
                        name: "fk_pfinder_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "gid");
                    table.ForeignKey(
                        name: "fk_pfinder_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_pfinder_group_id",
                table: "pfinder",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_pfinder_user_id",
                table: "pfinder",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pfinder");
        }
    }
}
