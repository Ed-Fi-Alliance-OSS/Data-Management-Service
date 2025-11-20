-- pgbench workload that repeatedly calls dms.InsertReferences.
-- Run with pgbench, e.g.:
--   PGPASSWORD='...' pgbench -h localhost -p 5432 -U postgres -d edfi_datamanagementservice \
--      --file=200_pgbench_concurrency.sql --clients=16 --jobs=4 --transactions=2000 --progress=10
-- All statements run in their own transaction so pgbench can report TPS/latency.

BEGIN;
SELECT * FROM dms.InsertReferences(
    (SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Test' ORDER BY Id DESC LIMIT 1),
    1::smallint,
    ARRAY[
        '9a5226cd-6f14-c117-73b0-575f5505790c',
        '0ae6e94d-d446-28f8-da03-240821ed958c',
        'b9c20540-8759-0edc-feec-1c7775711621',
        '0916dd27-b187-2c61-74c7-d88923aa800f',
        'e3f62f16-1c78-4d6d-4da0-9f10457a7f7a'
    ]::uuid[],
    ARRAY[12,12,1,15,10]::smallint[],
    FALSE
);
COMMIT;
