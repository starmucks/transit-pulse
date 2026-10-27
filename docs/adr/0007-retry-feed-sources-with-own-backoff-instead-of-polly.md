# 0007. Retry feed sources with our own backoff instead of Polly

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

The brief planned Polly (`Microsoft.Extensions.Resilience`) for retries and reconnection in the Ingestor. By task 1.8
the sources already survived failures on their own: a failed HTTP poll is logged and the next one runs on schedule,
an MQTT connection that worked is re-opened at once, and a source that throws is restarted by the pipeline. The
collector has run since 2026-10-24 without a process restart. What was missing: the restart delay was a fixed 5 s
(an hour-long broker outage meant ~720 attempts and error lines), a server asking to slow down (429) was polled on
schedule, and every failed poll logged a warning.

Two properties of the feeds rule out generic retry policies. A SIRI-VM request that timed out may have been answered
on the server, which then counts those changes as delivered; repeating it with the same `requestorId` silently loses
them, so every failure must reach the source, which starts a new subscription. And a source is not one operation: it
runs for days and fails rarely, so an attempt counter must reset after a good run.

## Decision

Feed sources use a small backoff written for this pipeline, with no transparent retries. A failing source is
restarted after 5 s, doubling up to 1 min, stretched by up to 20% at random so feeds that failed together do not
reconnect in lockstep, and back to 5 s after a run of at least a minute (`RestartBackoff`). An HTTP poll that fails
on the network is not repeated or delayed: the next poll runs on schedule. Only a server that asks is polled less
often: a `Retry-After` on 429 or 503 is followed, within the feed's interval and 30 min; a 429 without it doubles
the pause. Failures are logged per episode (start and end with a count), not per attempt.

## Alternatives considered

- **Standard resilience handler on `HttpClient`** (retries, circuit breaker, timeouts). Hides failed attempts from
  the source, so a retried SIRI request keeps its `requestorId` and loses changes; retries inside a 15–30 s interval
  duplicate the next poll; an open circuit stops polling, and each skipped snapshot is history lost for good.
- **Polly.Core retry pipeline around a source run.** Recognized library and built-in jitter, but its attempt count
  lives within one execution: resetting it after a healthy run needs a workaround around a ~50-line class, and adds
  a dependency to the AOT collector for no other use.
- **Back off HTTP polls after any failure.** Fewer requests during an outage, but recovery is noticed only after the
  current pause, so a short network loss turns into minutes of missing snapshots.

## Consequences

- No new dependency; the binary and AOT build are unchanged; the timing is tested on `FakeTimeProvider`.
- The backoff and throttling code is ours to maintain, and there is no circuit breaker; a broken feed keeps being
  polled on schedule, which is what lets it be picked up the moment it returns.
- After an outage of a push feed, reconnection can lag its return by up to the cap plus jitter (72 s); HSL
  messages in that window are not in the archive and cannot be replayed (ADR-0005). The cap is 1 min rather than
  the more usual 5 min for this reason: about 60 attempts an hour cost the public broker little.
- The decision covers feed sources in the Ingestor. Calls where a repeat is harmless (for example internal HTTP
  calls in later stages) may still use Polly.
