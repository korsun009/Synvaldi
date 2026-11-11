-- Single-statement migration batch, executed inside a transaction under an advisory lock.
CREATE TABLE IF NOT EXISTS account (
    id uuid PRIMARY KEY, username text NOT NULL, username_key text UNIQUE NOT NULL, secret text NOT NULL
);
CREATE TABLE IF NOT EXISTS device (
    id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES account ON DELETE CASCADE,
    name text NOT NULL, last_seen timestamptz NOT NULL DEFAULT now(), revoked boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS device_account ON device(account_id);
CREATE TABLE IF NOT EXISTS session (
    token_hash bytea PRIMARY KEY, device_id uuid NOT NULL REFERENCES device ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS session_device ON session(device_id);
CREATE TABLE IF NOT EXISTS profile (
    id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES account ON DELETE CASCADE,
    created_by uuid NOT NULL REFERENCES device, name text NOT NULL,
    revision bigint NOT NULL DEFAULT 0, fields jsonb NOT NULL DEFAULT '{}', UNIQUE(account_id,name)
);
CREATE INDEX IF NOT EXISTS profile_account ON profile(account_id);
CREATE TABLE IF NOT EXISTS admission (
    profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device ON DELETE CASCADE,
    PRIMARY KEY(profile_id, device_id)
);
CREATE TABLE IF NOT EXISTS focus_state (
    profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device ON DELETE CASCADE,
    payload jsonb NOT NULL,
    PRIMARY KEY(profile_id, device_id)
);
CREATE TABLE IF NOT EXISTS revision (
    profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE, number bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), device_name text NOT NULL, fields jsonb NOT NULL,
    PRIMARY KEY(profile_id, number)
);
CREATE INDEX IF NOT EXISTS revision_age ON revision(created_at);
CREATE TABLE IF NOT EXISTS operation (
    profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE, id uuid NOT NULL,
    device_id uuid NOT NULL REFERENCES device ON DELETE CASCADE,
    digest bytea NOT NULL, result jsonb NOT NULL, PRIMARY KEY(profile_id, id)
);
CREATE TABLE IF NOT EXISTS blob (
    account_id uuid NOT NULL REFERENCES account ON DELETE CASCADE,
    sha256 text NOT NULL, body bytea NOT NULL, PRIMARY KEY(account_id, sha256)
);

ALTER TABLE device ADD COLUMN IF NOT EXISTS active_profile uuid REFERENCES profile;
ALTER TABLE device ADD COLUMN IF NOT EXISTS browser_online boolean NOT NULL DEFAULT false;
ALTER TABLE device ADD COLUMN IF NOT EXISTS last_applied bigint NOT NULL DEFAULT 0;
ALTER TABLE focus_state ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE revision ADD COLUMN IF NOT EXISTS changed_keys jsonb NOT NULL DEFAULT '[]';
ALTER TABLE revision ADD COLUMN IF NOT EXISTS checkpoint boolean NOT NULL DEFAULT true;
ALTER TABLE revision ADD COLUMN IF NOT EXISTS previous_fields jsonb NOT NULL DEFAULT '{}';
CREATE INDEX IF NOT EXISTS revision_checkpoint ON revision(profile_id,number) WHERE checkpoint;
CREATE TABLE IF NOT EXISTS online_lease (
    id uuid PRIMARY KEY, device_id uuid NOT NULL UNIQUE REFERENCES device ON DELETE CASCADE,
    session_hash bytea NOT NULL REFERENCES session(token_hash) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS saved_snapshot (
    id uuid PRIMARY KEY, profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE,
    name text NOT NULL, revision bigint NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
    fields jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS saved_snapshot_profile ON saved_snapshot(profile_id);
CREATE TABLE IF NOT EXISTS trash (
    id uuid NOT NULL, profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE,
    name text NOT NULL, deleted_revision bigint NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
    fields jsonb NOT NULL, PRIMARY KEY(profile_id,id)
);
CREATE INDEX IF NOT EXISTS trash_age ON trash(created_at);
CREATE TABLE IF NOT EXISTS initial_read (
    profile_id uuid NOT NULL REFERENCES profile ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device ON DELETE CASCADE,
    revision bigint NOT NULL, PRIMARY KEY(profile_id,device_id)
);
