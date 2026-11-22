-- Warm storage for the live map and route punctuality (stage 3.1).
-- Cold full-resolution history stays in Parquet; Kafka remains the hot buffer.
--
-- Timescale unique indexes must include the partition time column, so stop_events
-- moves from PRIMARY KEY (id) to PRIMARY KEY (id, at). The Worker upsert deletes any
-- prior row for the same id in a short time window, then inserts (ADR-0014).

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Thinned vehicle positions (about one point / 10 s per vehicle; written in 3.2).
CREATE TABLE public.vehicle_positions (
    feed_id         text             NOT NULL,
    vehicle_id      text             NOT NULL,
    observed_at     timestamptz      NOT NULL,
    lon             double precision NOT NULL,
    lat             double precision NOT NULL,
    bearing         real,
    speed_mps       real,
    route_id        text,
    trip_id         text,
    delay_sec       integer,
    along_route_m   double precision,
    PRIMARY KEY (feed_id, vehicle_id, observed_at)
);

SELECT create_hypertable(
    'public.vehicle_positions',
    'observed_at',
    chunk_time_interval => INTERVAL '1 day',
    create_default_indexes => true);

CREATE INDEX vehicle_positions_feed_route_at
    ON public.vehicle_positions (feed_id, route_id, observed_at DESC)
    WHERE route_id IS NOT NULL;

ALTER TABLE public.vehicle_positions SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'feed_id, vehicle_id',
    timescaledb.compress_orderby = 'observed_at DESC');

-- Compress after two days; drop warm chunks after two weeks (local demo disk).
SELECT add_compression_policy('public.vehicle_positions', INTERVAL '2 days');
SELECT add_retention_policy('public.vehicle_positions', INTERVAL '14 days');

-- stop_events: allow delay for punctuality rollups; hypertable for CAGG + retention.
ALTER TABLE public.stop_events DROP CONSTRAINT stop_events_pkey;
ALTER TABLE public.stop_events ADD COLUMN delay_sec integer;
ALTER TABLE public.stop_events ADD PRIMARY KEY (id, at);

SELECT create_hypertable(
    'public.stop_events',
    'at',
    chunk_time_interval => INTERVAL '7 days',
    migrate_data => true,
    create_default_indexes => false);

ALTER TABLE public.stop_events SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'feed_id, route_id',
    timescaledb.compress_orderby = 'at DESC');

-- Events are small and feed C5 labels; keep longer than positions.
SELECT add_compression_policy('public.stop_events', INTERVAL '7 days');
SELECT add_retention_policy('public.stop_events', INTERVAL '90 days');

-- Daily punctuality rollup for the route page. delay_sec is filled when the sink has it;
-- rows without delay still count toward arrivals.
-- On-time window [-60, 180] s is a first demo default (early / up to 3 min late); revisit in 3.4 if needed.
CREATE MATERIALIZED VIEW public.punctuality_by_route_day
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 day', at) AS day,
    feed_id,
    route_id,
    count(*) FILTER (WHERE kind = 'arrival')::bigint AS arrivals,
    count(*) FILTER (
        WHERE kind = 'arrival'
          AND delay_sec IS NOT NULL
          AND delay_sec BETWEEN -60 AND 180)::bigint AS on_time_arrivals,
    avg(delay_sec) FILTER (WHERE kind = 'arrival') AS avg_arrival_delay_sec
FROM public.stop_events
GROUP BY 1, 2, 3
WITH NO DATA;

SELECT add_continuous_aggregate_policy(
    'public.punctuality_by_route_day',
    start_offset => INTERVAL '3 days',
    end_offset => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 hour');
