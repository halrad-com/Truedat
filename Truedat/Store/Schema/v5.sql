-- truedat store — schema v5 (2026-11-21). Forward-only; applied on top of v4 by SchemaMigrator inside one transaction,
-- after a VACUUM INTO backup of the v4 file. One added column and no data step: a store that was v4 holds no marker,
-- which is what NULL says.

-- How a track's 12 chroma values (hpcp12, thpcp12) were grouped from the extractor's 36: the catalog's `chromaFold`.
-- 2 = the corrected grouping (each note's three bins in one cell); NULL = written before the marker existed, so the
-- grouping is not recorded. Part of the audio description, so it lives once per media beside the values it describes.
ALTER TABLE track_content ADD COLUMN chromaFold INTEGER;
