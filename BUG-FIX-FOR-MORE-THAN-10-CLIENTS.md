# Fix: Enabling More Than 10 Concurrent Clients in edfi-performance-test

## Overview
The Ed-Fi performance test harness (`src/edfi-performance-test`) uses Locust's `FastHttpUser` to simulate API clients against the DMS service. Locust allocates a dedicated HTTP session (with its own connection pool) **per user**. That pool defaults to 10 sockets. Due to the way the harness cached this session, every simulated user was sharing the **same** pool, regardless of `--clientCount`. This created a hard cap of 10 concurrent sockets.

The fix ensures that each Locust user registers its own HTTP client with `EdFiAPIClient`, allowing the load generator to establish as many connections as users.

## Symptoms
- Running the load test with `--clientCount 20` (or `CLIENT_COUNT=20`) still produced only ~10 established TCP connections to the DMS frontend port (`ss -tan | grep :8080`).
- Locust's logs reported "Ramping to 20 users" but throughput remained identical to the 10-user scenario.

## Root Cause
Two places forced all users to share a single HTTP session:

1. **VolumeTestUser.on_start** (and the analogous pipeclean setup) cached the first user's HTTP session in the static `EdFiAPIClient.client` and skipped the rest of the initialization for subsequent users. Because `EdFiAPIClient.client` was never updated again, every user reused the first user's session/pool.
2. **EdFiTaskSet** always instantiated its API client with the static `EdFiAPIClient.client` instead of each user’s own `self.user.client`. Even after reassigning the global variable per user, nested task sets could still fall back to the stale session.

Since Locust’s `FastHttpUser` defaults to `concurrency=10`, the shared pool limited the entire run to 10 sockets.

## Fix
1. **Always rebind the current Locust user’s HTTP client in `VolumeTestUser.on_start`:**
   ```python
   def on_start(self):
       EdFiAPIClient.client = self.client
       if VolumeTestUser.is_initialized:
           return
       EdFiAPIClient.token = None
       …
   ```
   This ensures every user registers its own session before the shared one-time initialization runs.

2. **Instantiate `EdFiTaskSet` clients with the owning user’s session:**
   ```python
   locust_http_client = getattr(getattr(self, "user", None), "client", None)
   if locust_http_client is None:
       locust_http_client = EdFiAPIClient.client

   self._api_client = self.client_class(
       client=locust_http_client,
       token=EdFiAPIClient.token,
   )
   ```
   Using `self.user.client` guarantees that child task sets inherit the correct per-user session, while the fallback preserves compatibility if a task is instantiated outside a Locust run.

## Verification Steps
1. Rebuild the harness and restart the load test (`poetry run edfi-performance-test …`).
2. Once Locust reaches the target user count, inspect connections:
   ```bash
   watch -n1 "ss -tan | awk '$1==\"ESTAB\" && $4 ~ /:8080$/ {c++} END {print c+0}'"
   ```
   Expect ~20 established sockets for 20 users (numbers can temporarily exceed the user count because each FastHttpUser pool maintains idle connections).
3. Observe increased CPU utilization on the DMS service and PostgreSQL once the client-side bottleneck is removed.

## Result
With the fix applied, Locust now opens one HTTP session per user, each with its own 10-socket pool. This removes the 10-connection cap and allows higher `clientCount` values to drive proportionally higher load against DMS.
