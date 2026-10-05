using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalCommunicationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationAndMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationMembers_Conversatiosns_ConversationId",
                table: "ConversationMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Conversatiosns_ConversationId",
                table: "Messages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Conversatiosns",
                table: "Conversatiosns");

            migrationBuilder.RenameTable(
                name: "Conversatiosns",
                newName: "Conversations");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationMembers_Conversations_ConversationId",
                table: "ConversationMembers",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Conversations_ConversationId",
                table: "Messages",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationMembers_Conversations_ConversationId",
                table: "ConversationMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Conversations_ConversationId",
                table: "Messages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations");

            migrationBuilder.RenameTable(
                name: "Conversations",
                newName: "Conversatiosns");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Conversatiosns",
                table: "Conversatiosns",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationMembers_Conversatiosns_ConversationId",
                table: "ConversationMembers",
                column: "ConversationId",
                principalTable: "Conversatiosns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Conversatiosns_ConversationId",
                table: "Messages",
                column: "ConversationId",
                principalTable: "Conversatiosns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
