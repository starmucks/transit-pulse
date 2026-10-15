-- Runs once, when the data volume is initialized for the first time.
-- Aspire creates application databases later, from template1, so the extensions go there
-- and every new database inherits them.
-- After bumping the image tag, run ALTER EXTENSION ... UPDATE in each database: this script does not run again.
\connect template1
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS postgis;
