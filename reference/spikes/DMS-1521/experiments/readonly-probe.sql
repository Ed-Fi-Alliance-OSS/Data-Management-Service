\set ON_ERROR_STOP 0
DROP DATABASE IF EXISTS dms1521_ro_probe;
CREATE DATABASE dms1521_ro_probe;
\c dms1521_ro_probe
CREATE TABLE doc ("DocumentId" bigint PRIMARY KEY, v int);
CREATE TABLE child ("Doc_DocumentId" bigint, x int);
CREATE INDEX ON child ("Doc_DocumentId");
INSERT INTO doc SELECT g, g FROM generate_series(1,1000) g;
INSERT INTO child SELECT g, g FROM generate_series(1,1000) g;
ALTER DATABASE dms1521_ro_probe SET default_transaction_read_only = on;
\c dms1521_ro_probe
SHOW default_transaction_read_only;
\echo '--- baseline prelude (expect 25006)'
BEGIN;
DROP TABLE IF EXISTS "page";
ROLLBACK;
\echo '--- inline query keyset + reader in one transaction'
BEGIN;
WITH page_ids AS (SELECT r."DocumentId" FROM doc r ORDER BY r."DocumentId" LIMIT 25 OFFSET 0)
SELECT count(*) AS selected FROM (SELECT set_config('dms.page_keyset', COALESCE(string_agg(CAST(p."DocumentId" AS text), ',' ORDER BY p."DocumentId"), ''), true) AS keyset FROM page_ids p) c
CROSS JOIN LATERAL unnest(CAST(string_to_array(c.keyset, ',') AS bigint[])) AS u("DocumentId");
SELECT count(*) AS child_rows FROM child t INNER JOIN (SELECT u."DocumentId" FROM unnest(COALESCE(CAST(NULL AS bigint[]), CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[]))) WITH ORDINALITY AS u("DocumentId", "Ordinal")) k ON t."Doc_DocumentId" = k."DocumentId";
COMMIT;
\echo '--- inline selected page (bigint[])'
BEGIN;
SELECT count(*) AS child_rows FROM child t INNER JOIN (SELECT u."DocumentId" FROM unnest(COALESCE(CAST('{3,1,2}' AS bigint[]), CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[]))) WITH ORDINALITY AS u("DocumentId", "Ordinal")) k ON t."Doc_DocumentId" = k."DocumentId";
COMMIT;
\c postgres
DROP DATABASE dms1521_ro_probe;
