CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

CREATE TABLE plan_classpath (
    plan_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    path    TEXT NOT NULL,
    PRIMARY KEY (plan_id, ordinal)
) STRICT, WITHOUT ROWID;

CREATE TABLE plan_directories (
    plan_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    path    TEXT NOT NULL,
    PRIMARY KEY (plan_id, ordinal)
) STRICT, WITHOUT ROWID;

CREATE TABLE launch_args (
    plan_id INTEGER NOT NULL,
    phase   TEXT NOT NULL CHECK (phase IN ('jvm', 'jvm_defaults', 'game')),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    value   TEXT NOT NULL,
    PRIMARY KEY (plan_id, phase, ordinal)
) STRICT, WITHOUT ROWID;
