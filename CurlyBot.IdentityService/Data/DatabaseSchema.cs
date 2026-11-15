namespace CurlyBot.IdentityService.Data;

internal static class DatabaseSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS platform_accounts (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            platform text NOT NULL,
            external_id text NOT NULL,
            username text NOT NULL DEFAULT '',
            username_normalized text NOT NULL DEFAULT '',
            display_name text NOT NULL DEFAULT '',
            display_name_normalized text NOT NULL DEFAULT '',
            link_group_id uuid NULL,
            dm_blocked boolean NOT NULL DEFAULT false,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            last_contact_at timestamptz NULL,
            contact_count bigint NOT NULL DEFAULT 0,
            CONSTRAINT ux_platform_accounts_platform_external UNIQUE (platform, external_id)
        );

        CREATE INDEX IF NOT EXISTS ix_platform_accounts_username
            ON platform_accounts (platform, username_normalized);

        CREATE INDEX IF NOT EXISTS ix_platform_accounts_display_name
            ON platform_accounts (platform, display_name_normalized);

        CREATE INDEX IF NOT EXISTS ix_platform_accounts_link_group
            ON platform_accounts (link_group_id);

        CREATE TABLE IF NOT EXISTS link_requests (
            id uuid PRIMARY KEY,
            source_account_id uuid NOT NULL REFERENCES platform_accounts(id) ON DELETE CASCADE,
            target_account_id uuid NOT NULL REFERENCES platform_accounts(id) ON DELETE CASCADE,
            status text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            expires_at timestamptz NOT NULL,
            responded_at timestamptz NULL,
            reason text NULL
        );

        CREATE INDEX IF NOT EXISTS ix_link_requests_source
            ON link_requests (source_account_id, status);

        CREATE INDEX IF NOT EXISTS ix_link_requests_target
            ON link_requests (target_account_id, status);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_link_requests_pending_pair
            ON link_requests (source_account_id, target_account_id)
            WHERE status = 'pending';

        CREATE TABLE IF NOT EXISTS link_cooldowns (
            source_account_id uuid NOT NULL REFERENCES platform_accounts(id) ON DELETE CASCADE,
            target_account_id uuid NOT NULL REFERENCES platform_accounts(id) ON DELETE CASCADE,
            until timestamptz NOT NULL,
            PRIMARY KEY (source_account_id, target_account_id)
        );
        """;
}
