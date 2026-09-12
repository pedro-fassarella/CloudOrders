using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudOrders.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    consumer_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_messages", x => new { x.consumer_name, x.message_id });
                    table.CheckConstraint("CK_processed_messages_state", "state IN ('Processing', 'Completed')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_messages");
        }
    }
}
