-- ReviewMemory: re-indexado incremental (roadmap ítem 2).
-- content_hash permite conciliar hilos entrantes vs existentes sin borrarlos.

ALTER TABLE review_threads ADD COLUMN IF NOT EXISTS content_hash text;
