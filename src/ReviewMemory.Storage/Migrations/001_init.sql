-- ReviewMemory: memoria institucional de code review.
-- Etapa 1: PRs, archivos, hunks, discusiones de review y decisiones.

CREATE TABLE IF NOT EXISTS pull_requests (
    repo       text        NOT NULL,
    number     int         NOT NULL,
    title      text        NOT NULL,
    body       text        NOT NULL DEFAULT '',
    author     text        NOT NULL,
    state      text        NOT NULL CHECK (state IN ('open', 'closed', 'merged')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    merged_at  timestamptz,
    indexed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (repo, number)
);

CREATE TABLE IF NOT EXISTS pr_files (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pr_repo   text   NOT NULL,
    pr_number int    NOT NULL,
    path      text   NOT NULL,
    additions int    NOT NULL DEFAULT 0,
    deletions int    NOT NULL DEFAULT 0,
    patch     text,
    FOREIGN KEY (pr_repo, pr_number)
        REFERENCES pull_requests (repo, number) ON DELETE CASCADE,
    UNIQUE (pr_repo, pr_number, path)
);

CREATE INDEX IF NOT EXISTS idx_pr_files_path ON pr_files (path);

CREATE TABLE IF NOT EXISTS code_hunks (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    file_id   bigint NOT NULL REFERENCES pr_files (id) ON DELETE CASCADE,
    old_start int    NOT NULL,
    old_lines int    NOT NULL,
    new_start int    NOT NULL,
    new_lines int    NOT NULL,
    body      text   NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_code_hunks_file ON code_hunks (file_id);

CREATE TABLE IF NOT EXISTS review_threads (
    id         bigint      PRIMARY KEY,
    pr_repo    text        NOT NULL,
    pr_number  int         NOT NULL,
    path       text        NOT NULL,
    line       int,
    resolved   boolean     NOT NULL DEFAULT false,
    author     text        NOT NULL,
    finding    text        NOT NULL,
    created_at timestamptz NOT NULL,
    search_vec tsvector GENERATED ALWAYS AS (
        to_tsvector('english', finding || ' ' || replace(path, '/', ' '))
    ) STORED,
    FOREIGN KEY (pr_repo, pr_number)
        REFERENCES pull_requests (repo, number) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_review_threads_pr ON review_threads (pr_repo, pr_number);
CREATE INDEX IF NOT EXISTS idx_review_threads_fts ON review_threads USING gin (search_vec);

CREATE TABLE IF NOT EXISTS review_comments (
    id         bigint      PRIMARY KEY,
    thread_id  bigint      NOT NULL REFERENCES review_threads (id) ON DELETE CASCADE,
    author     text        NOT NULL,
    body       text        NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_review_comments_thread ON review_comments (thread_id, created_at);

CREATE TABLE IF NOT EXISTS decisions (
    thread_id  bigint PRIMARY KEY REFERENCES review_threads (id) ON DELETE CASCADE,
    outcome    text   NOT NULL CHECK (outcome IN ('accepted', 'rejected', 'partially_accepted', 'unknown')),
    reason     text,
    confidence text   NOT NULL DEFAULT 'inferred' CHECK (confidence IN ('inferred', 'manual')),
    decided_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_decisions_outcome ON decisions (outcome);
