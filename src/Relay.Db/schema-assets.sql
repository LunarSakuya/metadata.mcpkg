PRAGMA foreign_keys = ON;

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

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

CREATE VIEW v_asset_objects AS
SELECT d.index_id, o.path, o.sha1, o.size,
       'https://resources.download.minecraft.net/' || substr(o.sha1, 1, 2) || '/' || o.sha1 AS url
FROM asset_docs d
JOIN asset_objects o ON o.doc_id = d.index_id;
