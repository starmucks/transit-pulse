-- Stop panel (stage 3.4): recent observed events filtered by stop_id.
-- The existing index is (feed_id, route_id, at) for route-oriented reads and the CAGG source.

CREATE INDEX stop_events_feed_stop_at
    ON public.stop_events (feed_id, stop_id, at DESC);
