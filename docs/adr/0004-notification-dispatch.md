# ADR-0004: Bounded notification dispatch

Accepted, 12 September 2026. The original asynchronous eviction-listener choice is superseded by [ADR0012](0012-notification-and-map-contracts.md); this dispatcher now describes removal diagnostics.

`BoundedNotificationDispatcher<TNotification>` uses a BCL queue, a short gate and one active worker. Full admission drops the newest event and returns false while incrementing `DroppedFull`. Accepted events retain FIFO order and at-most-once invocation, not guaranteed delivery. Capture and dispatcher queues have separate bounds; see the [resource model](../resource-model.md).

Enqueue and the worker's empty-to-idle handoff share the gate. Reset scheduled/active ownership atomically; an old worker's `finally` cannot clear a successor's state. Default scheduling uses `ThreadPool.UnsafeQueueUserWorkItem` without request context capture. Scheduler rejection/exception drops and counts the current queue; later admission may retry scheduling. Do not invoke a slow handler inline as a rejection fallback.

Handlers run outside locks. Exceptions are observed/counted and dispatch continues. Disposal stops admission and drops queued events, but does not wait for a running user handler or dispose its value. Snapshots distinguish invoked/delivered/failed/dropped/queued state. Bounded memory, non-blocking producers and arbitrarily slow handlers preclude exactly-once delivery.

`NotificationDispatcherTests`, `ListenerIntegrationTests` and `MetricsTests` cover saturation, FIFO, handoff, rejection, throwing/slow/re-entrant handlers and shutdown. Listeners are diagnostics, not ownership protocols; use leases for automatic disposal. This is an independent BCL composition, not a translated upstream queue.
