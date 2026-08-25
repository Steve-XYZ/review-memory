-- ReviewMemory: incremental re-indexing (roadmap item 2).
-- content_hash reconciles incoming threads against existing ones without deleting them.

ALTER TABLE review_threads ADD COLUMN IF NOT EXISTS content_hash text;
