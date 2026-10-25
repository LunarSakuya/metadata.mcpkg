PRAGMA foreign_keys = ON;

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

CREATE TABLE platforms (
    ordinal INTEGER PRIMARY KEY,
    token   TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE versions (
    id                   TEXT PRIMARY KEY,
    key                  TEXT NOT NULL UNIQUE,
    type                 TEXT NOT NULL,
    release_time         TEXT NOT NULL,
    java_major           INTEGER NOT NULL CHECK (java_major >= 0),
    java_defaulted       INTEGER NOT NULL CHECK (java_defaulted IN (0, 1)),
    assets_index         TEXT,
    server_available     INTEGER NOT NULL CHECK (server_available IN (0, 1)),
    revision             TEXT NOT NULL,
    source_manifest_sha1 TEXT,
    schema_version       INTEGER NOT NULL
) STRICT;

CREATE TABLE plans (
    id             INTEGER PRIMARY KEY,
    version_id     TEXT NOT NULL REFERENCES versions(id) ON DELETE CASCADE,
    kind           TEXT NOT NULL CHECK (kind IN ('client', 'server')),
    platform       TEXT NOT NULL,
    revision       TEXT NOT NULL,
    total_bytes    INTEGER NOT NULL CHECK (total_bytes >= 0),
    main_class     TEXT,
    assets_index   TEXT,
    assets_mode    TEXT,
    assets_root    TEXT,
    assets_document TEXT,
    assets_sha1    TEXT,
    assets_size    INTEGER CHECK (assets_size IS NULL OR assets_size >= 0),
    logging_argument TEXT,
    logging_path     TEXT,
    native_dir       TEXT,
    UNIQUE (version_id, kind, platform)
) STRICT;

CREATE INDEX ix_plans_version ON plans(version_id, kind, platform);
CREATE INDEX ix_plans_assets ON plans(assets_index);

CREATE TABLE contents (
    id   INTEGER PRIMARY KEY,
    sha1 TEXT NOT NULL UNIQUE CHECK (length(sha1) = 40),
    size INTEGER NOT NULL CHECK (size >= 0)
) STRICT;

CREATE TABLE content_urls (
    content_id INTEGER NOT NULL REFERENCES contents(id) ON DELETE CASCADE,
    ordinal    INTEGER NOT NULL CHECK (ordinal >= 0),
    url        TEXT NOT NULL,
    PRIMARY KEY (content_id, ordinal)
) STRICT;

CREATE TABLE files (
    plan_id    INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    role       TEXT NOT NULL,
    path       TEXT NOT NULL,
    content_id INTEGER NOT NULL REFERENCES contents(id),
    PRIMARY KEY (plan_id, path)
) STRICT, WITHOUT ROWID;

CREATE TABLE plan_classpath (
    plan_id INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    path    TEXT NOT NULL,
    PRIMARY KEY (plan_id, ordinal)
) STRICT, WITHOUT ROWID;

CREATE TABLE plan_directories (
    plan_id INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    path    TEXT NOT NULL,
    PRIMARY KEY (plan_id, ordinal)
) STRICT, WITHOUT ROWID;

CREATE TABLE launch_args (
    plan_id INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    phase   TEXT NOT NULL CHECK (phase IN ('jvm', 'jvm_defaults', 'game')),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    value   TEXT NOT NULL,
    PRIMARY KEY (plan_id, phase, ordinal)
) STRICT, WITHOUT ROWID;

CREATE TABLE natives (
    plan_id INTEGER PRIMARY KEY REFERENCES plans(id) ON DELETE CASCADE,
    dir     TEXT NOT NULL
) STRICT;

CREATE TABLE extract_exclude (
    plan_id INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    path    TEXT NOT NULL,
    PRIMARY KEY (plan_id, path)
) STRICT, WITHOUT ROWID;

CREATE TABLE asset_docs (
    index_id    TEXT PRIMARY KEY,
    mode        TEXT NOT NULL,
    root        TEXT NOT NULL,
    sha1        TEXT NOT NULL CHECK (length(sha1) = 40),
    total_bytes INTEGER NOT NULL CHECK (total_bytes >= 0),
    revision    TEXT NOT NULL,
    schema_version INTEGER NOT NULL
) STRICT;

CREATE TABLE asset_objects (
    doc_id TEXT NOT NULL REFERENCES asset_docs(index_id) ON DELETE CASCADE,
    sha1   TEXT NOT NULL CHECK (length(sha1) = 40),
    size   INTEGER NOT NULL CHECK (size >= 0),
    path   TEXT GENERATED ALWAYS AS ('assets/objects/' || substr(sha1, 1, 2) || '/' || sha1) VIRTUAL,
    PRIMARY KEY (doc_id, sha1)
) STRICT, WITHOUT ROWID;

CREATE TABLE asset_links (
    doc_id    TEXT NOT NULL REFERENCES asset_docs(index_id) ON DELETE CASCADE,
    ordinal   INTEGER NOT NULL CHECK (ordinal >= 0),
    from_path TEXT NOT NULL,
    to_path   TEXT NOT NULL,
    PRIMARY KEY (doc_id, ordinal)
) STRICT;

CREATE VIEW v_plan_files AS
SELECT
    p.id AS plan_id,
    p.kind,
    p.platform,
    v.id AS version_id,
    v.key AS version_key,
    f.role,
    f.path,
    c.sha1,
    c.size
FROM plans p
JOIN versions v ON v.id = p.version_id
JOIN files f ON f.plan_id = p.id
JOIN contents c ON c.id = f.content_id;

CREATE VIEW v_file_urls AS
SELECT
    p.id AS plan_id,
    v.key AS version_key,
    p.kind,
    p.platform,
    f.path,
    c.sha1,
    c.size,
    u.ordinal,
    u.url
FROM plans p
JOIN versions v ON v.id = p.version_id
JOIN files f ON f.plan_id = p.id
JOIN contents c ON c.id = f.content_id
JOIN content_urls u ON u.content_id = c.id;

CREATE VIEW v_asset_objects AS
SELECT
    d.index_id,
    o.path,
    o.sha1,
    o.size,
    'https://resources.download.minecraft.net/' || substr(o.sha1, 1, 2) || '/' || o.sha1 AS url
FROM asset_docs d
JOIN asset_objects o ON o.doc_id = d.index_id;
