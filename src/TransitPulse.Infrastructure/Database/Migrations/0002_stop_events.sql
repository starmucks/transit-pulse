-- Stop arrivals, departures and passes written by the Worker from events.stop.
-- Upsert key is the deterministic event id from VehicleStopEvent (ADR-0014).
-- Hypertable conversion belongs to stage 3; a plain table is enough to prove no duplicates.

CREATE TABLE public.stop_events (
    id            text        PRIMARY KEY,
    feed_id       text        NOT NULL,
    vehicle_id    text        NOT NULL,
    trip_id       text        NOT NULL,
    route_id      text        NOT NULL,
    direction_id  smallint    NOT NULL CHECK (direction_id IN (0, 1)),
    service_date  date        NOT NULL,
    stop_id       text        NOT NULL,
    stop_sequence integer     NOT NULL,
    kind          text        NOT NULL CHECK (kind IN ('arrival', 'departure', 'passed')),
    at            timestamptz NOT NULL,
    written_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX stop_events_feed_route_at ON public.stop_events (feed_id, route_id, at);
