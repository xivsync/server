using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class moodlesshare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "moodles",
                columns: table => new
                {
                    guid = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    icon_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<int>(type: "integer", nullable: false),
                    applier = table.Column<string>(type: "text", nullable: true),
                    dispelable = table.Column<bool>(type: "boolean", nullable: false),
                    stacks = table.Column<int>(type: "integer", nullable: false),
                    status_on_dispell = table.Column<Guid>(type: "uuid", nullable: false),
                    custom_fx_path = table.Column<string>(type: "text", nullable: true),
                    stack_on_reapply = table.Column<bool>(type: "boolean", nullable: false),
                    stacks_inc_on_reapply = table.Column<int>(type: "integer", nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    seconds = table.Column<int>(type: "integer", nullable: false),
                    no_expire = table.Column<bool>(type: "boolean", nullable: false),
                    as_permanent = table.Column<bool>(type: "boolean", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moodles", x => x.guid);
                    table.ForeignKey(
                        name: "fk_moodles_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_moodles_user_uid",
                table: "moodles",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moodles");
        }
    }
}
