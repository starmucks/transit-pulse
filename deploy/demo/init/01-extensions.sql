-- Runs once when the Timescale data volume is first initialized.
-- Application databases inherit extensions from template1 (same as Aspire AppHost).
\connect template1
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS postgis;
