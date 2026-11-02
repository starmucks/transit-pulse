-- GTFS schedules. Every imported zip is a snapshot; each feed has at most one active snapshot.
-- Data tables are LIST-partitioned by snapshot_id: an import copies into fresh partitions and
-- an old version goes away with DROP TABLE of its partitions, not a DELETE of millions of rows.
--
-- No foreign keys on data tables: GtfsSnapshotCheck validates references while the import copies,
-- and a per-row FK check would slow COPY of ~11 million HSL stop_times.

CREATE SCHEMA gtfs;

CREATE TABLE gtfs.snapshot (
    snapshot_id integer     GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    feed_id     text        NOT NULL,
    file_name   text        NOT NULL,
    sha256      bytea       NOT NULL,
    imported_at timestamptz NOT NULL DEFAULT now(),
    is_active   boolean     NOT NULL DEFAULT false,
    -- The same zip is imported once; a re-import of it is a no-op.
    UNIQUE (feed_id, sha256)
);

CREATE UNIQUE INDEX snapshot_one_active_per_feed ON gtfs.snapshot (feed_id) WHERE is_active;

CREATE TABLE gtfs.routes (
    snapshot_id integer NOT NULL,
    route_id    text    NOT NULL,
    short_name  text,
    long_name   text,
    route_type  integer NOT NULL,
    PRIMARY KEY (snapshot_id, route_id)
) PARTITION BY LIST (snapshot_id);

CREATE TABLE gtfs.stops (
    snapshot_id    integer  NOT NULL,
    stop_id        text     NOT NULL,
    name           text,
    lat            double precision,
    lon            double precision,
    location_type  smallint NOT NULL,
    parent_station text,
    PRIMARY KEY (snapshot_id, stop_id)
) PARTITION BY LIST (snapshot_id);

CREATE TABLE gtfs.trips (
    snapshot_id  integer  NOT NULL,
    trip_id      text     NOT NULL,
    route_id     text     NOT NULL,
    service_id   text     NOT NULL,
    direction_id smallint,
    shape_id     text,
    headsign     text,
    PRIMARY KEY (snapshot_id, trip_id)
) PARTITION BY LIST (snapshot_id);

-- Times are seconds since the start of the service day and may exceed 86400 (25:10:00).
-- integer, not interval: 4 bytes instead of 16 on the largest table.
CREATE TABLE gtfs.stop_times (
    snapshot_id         integer NOT NULL,
    trip_id             text    NOT NULL,
    stop_sequence       integer NOT NULL,
    stop_id             text    NOT NULL,
    arrival_sec         integer,
    departure_sec       integer,
    shape_dist_traveled double precision,
    PRIMARY KEY (snapshot_id, trip_id, stop_sequence)
) PARTITION BY LIST (snapshot_id);

CREATE TABLE gtfs.shapes (
    snapshot_id   integer          NOT NULL,
    shape_id      text             NOT NULL,
    sequence      integer          NOT NULL,
    lat           double precision NOT NULL,
    lon           double precision NOT NULL,
    dist_traveled double precision,
    PRIMARY KEY (snapshot_id, shape_id, sequence)
) PARTITION BY LIST (snapshot_id);

-- days is the GtfsWeekdays bit mask: Monday = 1 ... Sunday = 64.
CREATE TABLE gtfs.calendar (
    snapshot_id integer  NOT NULL,
    service_id  text     NOT NULL,
    days        smallint NOT NULL,
    start_date  date     NOT NULL,
    end_date    date     NOT NULL,
    PRIMARY KEY (snapshot_id, service_id)
) PARTITION BY LIST (snapshot_id);

CREATE TABLE gtfs.calendar_dates (
    snapshot_id integer NOT NULL,
    service_id  text    NOT NULL,
    date        date    NOT NULL,
    added       boolean NOT NULL,
    PRIMARY KEY (snapshot_id, service_id, date)
) PARTITION BY LIST (snapshot_id);
