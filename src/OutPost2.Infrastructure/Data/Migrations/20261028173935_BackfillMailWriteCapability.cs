using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutPost2.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives existing Microsoft connections the new <c>mail.write</c> capability.
    /// <para>
    /// Capabilities live in a JSONB column, so no schema changes — but a connection without
    /// the row is invisible to the resolver, and the user is told "no connection set up for
    /// that yet" about a mailbox that is sitting right there. Every mailbox would need
    /// re-authorising to earn a capability its token already has.
    /// </para>
    /// <para>
    /// Granted is copied from <c>mail.read</c>, which is the most that can be recovered: the
    /// granted scope list is not kept after consent, so a connection holding only Mail.Read
    /// is indistinguishable here from one holding Mail.ReadWrite. Every connection to date
    /// was consented against a scope set that asks for ReadWrite, and where that is somehow
    /// wrong Graph refuses the call with a 403 that names the missing scope. New connections
    /// do not go through this path — they read the real grant from the token response.
    /// </para>
    /// </summary>
    public partial class BackfillMailWriteCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "connections"
                SET "Capabilities" = "Capabilities" || jsonb_build_array(jsonb_build_object(
                        'Name', 'mail.write',
                        'Granted', ("Capabilities" @> '[{"Name":"mail.read","Granted":true}]'),
                        'Enabled', true,
                        'IsSelfImposedOnly', false))
                WHERE "Capabilities" IS NOT NULL
                  AND "Capabilities" @> '[{"Name":"mail.read"}]'
                  AND NOT ("Capabilities" @> '[{"Name":"mail.write"}]');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "connections"
                SET "Capabilities" = (
                        SELECT COALESCE(jsonb_agg(c), '[]'::jsonb)
                        FROM jsonb_array_elements("Capabilities") AS c
                        WHERE c->>'Name' <> 'mail.write')
                WHERE "Capabilities" IS NOT NULL
                  AND "Capabilities" @> '[{"Name":"mail.write"}]';
                """);
        }
    }
}
