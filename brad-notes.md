REPLICA IDENTITY FULL adds ~5-10% write overhead ??

-----


-- Set autovacuum parameters for Reference table (high churn expected)
ALTER TABLE dms.Reference SET (
    autovacuum_vacuum_scale_factor = 0.05,  -- Vacuum more aggressively
    autovacuum_analyze_scale_factor = 0.02, -- Analyze more frequently  
    autovacuum_vacuum_cost_delay = 10       -- Balance vacuum load
);


------------

  1. Try python perf-claude/data/generate_deterministic_data.py --documents 1000 --references 5000 --output ./perf-claude/data/test-out and
     then ./perf-claude/scripts/load-test-data-from-csv.sh ./perf-claude/data/test-out to validate the pipeline.
  2. Wire the loader into any existing automation (e.g., quick-start.sh) if you want the fast path as the default.

------------


  1. Rerun perf-claude/scripts/run-all-tests.sh or targeted scenarios to regenerate reports with the richer metrics.
  2. Inspect results/*/analysis_report.txt to confirm histograms and percentile fields behave as expected under real workloads.

------------

  Next steps (manual): run ./scripts/generate-test-data.sh --mode csv for a smoke check, or ./scripts/generate-test-data.sh --mode sql --chunk-
  size 1000 --resume to validate the chunked loader end-to-end.


------------

  1. Recreate or update the perf DB (./scripts/setup-test-db.sh --force) and rerun either data loader so the new fixtures populate, then
     execute the baseline SQL test to confirm stable results.
  2. When adding new scenarios, extend dms.build_perf_reference_targets() and call psql -c "SELECT dms.build_perf_reference_targets();" to
     rebuild the table; the alternative SQL scenarios now pull directly from these fixtures, so keep them in sync.



----

This is the first time running these scripts so I expect issues with them that will need to be worked out, like syntax errors or problems
  with bash escaping. Use perf-claude/EXECUTION_NOTES.md to track interesting environment notes, and what problems you run into and how you
  solved them. PostgreSQL is running in a Docker container named dms-postgresql and perf-claude/scripts/config.sh has the correct database
  configuration information as defaults. The postgresql-contrib package is already installed in the container. Note this information in your
  perf-claude/EXECUTION_NOTES.md file and run the setup script. Note and fix any problems you run into with the script.
 

TODOs:
-----

* We are getting the following while running "generate-test-data.sh --mode csv" :

 checkpoints are occurring too frequently (16 seconds apart)

2025-10-18 05:12:48.461 UTC [27] HINT:  Consider increasing the configuration parameter "max_wal_size".

* generated CSVs need to be LF not CRLF



Questions:
---------

CSV for 20 million Reference table rows is 1.1 GB, is that too big?




NOW: Review what just happened, check table sizes


codex resume 0199f808-79ee-7e81-a247-a894afd34f31
