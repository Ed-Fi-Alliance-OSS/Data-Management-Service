#!/usr/bin/env python3

import asyncio
import asyncpg
import time
import json
import statistics
import sys
import os
from datetime import datetime
from typing import List, Dict, Any
import argparse
import random

# Configuration from environment
DB_HOST = os.getenv('DB_HOST', 'localhost')
DB_PORT = int(os.getenv('DB_PORT', '5432'))
DB_NAME = os.getenv('DB_NAME', 'dms_perf_test')
DB_USER = os.getenv('DB_USER', 'postgres')
DB_PASSWORD = os.getenv('DB_PASSWORD', 'postgres')

class LoadTester:
    def __init__(self, pool_size: int = 10):
        self.pool_size = pool_size
        self.pool = None
        self.document_ids = []
        self.alias_ids = []
        self.sample_seed = 12345

    async def _init_conn(self, conn: asyncpg.Connection):
        """Configure session-level settings for observability."""
        await conn.execute("SET track_functions = 'pl';")

    async def init_pool(self):
        """Initialize the connection pool"""
        self.pool = await asyncpg.create_pool(
            host=DB_HOST,
            port=DB_PORT,
            database=DB_NAME,
            user=DB_USER,
            password=DB_PASSWORD,
            min_size=self.pool_size,
            max_size=self.pool_size * 2,
            command_timeout=60,
            server_settings={"application_name": "perf_concurrent_load"},
            init=self._init_conn,
        )

    async def load_test_data(self):
        """Load document and alias IDs for testing"""
        async with self.pool.acquire() as conn:
            # Get sample document IDs
            rows = await conn.fetch("""
                SELECT Id, DocumentPartitionKey
                FROM dms.Document
                ORDER BY DocumentPartitionKey, Id
                LIMIT 100000
            """)
            self.document_ids = [(row['id'], row['documentpartitionkey']) for row in rows]

            # Get sample alias IDs
            rows = await conn.fetch("""
                SELECT ReferentialId, ReferentialPartitionKey
                FROM dms.Alias
                ORDER BY ReferentialPartitionKey, ReferentialId
                LIMIT 1000000
            """)
            self.alias_ids = [(row['referentialid'], row['referentialpartitionkey']) for row in rows]

        sampler = random.Random(self.sample_seed)
        sampler.shuffle(self.document_ids)
        sampler.shuffle(self.alias_ids)

        print(f"Loaded {len(self.document_ids)} documents and {len(self.alias_ids)} aliases for testing")

    async def update_references_current(self, session_id: int, iterations: int):
        """Simulate reference updates using current implementation"""
        rng = random.Random(self.sample_seed + session_id)
        async with self.pool.acquire() as conn:
            await conn.execute(
                "SET application_name = $1",
                f"perf_concurrent_load_{session_id}",
            )
            latencies = []

            for i in range(iterations):
                # Pick a random document
                doc_id, doc_partition = rng.choice(self.document_ids)

                # Pick random references (simulate varying reference counts)
                num_refs = rng.randint(10, 100)
                selected_refs = rng.sample(self.alias_ids, min(num_refs, len(self.alias_ids)))

                if not selected_refs:
                    await asyncio.sleep(rng.uniform(0.01, 0.05))
                    continue

                parent_ids = [doc_id] * len(selected_refs)
                parent_partitions = [doc_partition] * len(selected_refs)
                ref_ids = [str(ref[0]) for ref in selected_refs]
                ref_partitions = [ref[1] for ref in selected_refs]

                start = time.time()
                try:
                    # Call the stored procedure
                    await conn.fetch("""
                        SELECT dms.InsertReferences(
                            $1::bigint[],
                            $2::smallint[],
                            $3::uuid[],
                            $4::smallint[]
                        )
                    """, parent_ids, parent_partitions, ref_ids, ref_partitions)

                    latency = (time.time() - start) * 1000  # Convert to ms
                    latencies.append(latency)

                    if i % 10 == 0:
                        print(f"Session {session_id}: Completed {i+1}/{iterations} operations")

                except Exception as e:
                    print(f"Session {session_id}: Error in iteration {i}: {e}")
                    latencies.append(-1)  # Mark as error

                # Small delay between operations
                await asyncio.sleep(rng.uniform(0.01, 0.05))

            return {
                'session_id': session_id,
                'iterations': iterations,
                'latencies': latencies,
                'successful': len([l for l in latencies if l > 0]),
                'failed': len([l for l in latencies if l < 0])
            }

    async def run_concurrent_test(self, num_sessions: int, iterations_per_session: int):
        """Run concurrent load test"""
        print(f"\nStarting concurrent load test with {num_sessions} sessions")
        print(f"Each session will perform {iterations_per_session} operations")

        start_time = time.time()

        # Create tasks for all sessions
        tasks = [
            self.update_references_current(i, iterations_per_session)
            for i in range(num_sessions)
        ]

        # Run all sessions concurrently
        session_results = await asyncio.gather(*tasks)

        total_time = time.time() - start_time

        # Aggregate results
        all_latencies = []
        total_successful = 0
        total_failed = 0

        for result in session_results:
            successful_latencies = [l for l in result['latencies'] if l > 0]
            all_latencies.extend(successful_latencies)
            total_successful += result['successful']
            total_failed += result['failed']

        # Calculate statistics
        if all_latencies:
            stats = {
                'total_operations': total_successful + total_failed,
                'successful_operations': total_successful,
                'failed_operations': total_failed,
                'total_time_seconds': total_time,
                'operations_per_second': total_successful / total_time,
                'latency_min_ms': min(all_latencies),
                'latency_max_ms': max(all_latencies),
                'latency_mean_ms': statistics.mean(all_latencies),
                'latency_median_ms': statistics.median(all_latencies),
                'latency_stdev_ms': statistics.stdev(all_latencies) if len(all_latencies) > 1 else 0,
                'latency_p95_ms': statistics.quantiles(all_latencies, n=20)[18] if len(all_latencies) > 20 else max(all_latencies),
                'latency_p99_ms': statistics.quantiles(all_latencies, n=100)[98] if len(all_latencies) > 100 else max(all_latencies),
            }

            return stats, all_latencies
        else:
            print("No successful operations completed!")
            return None, []

    async def save_results(self, test_name: str, stats: Dict[str, Any], latencies: List[float]):
        """Save test results to database"""
        async with self.pool.acquire() as conn:
            await conn.execute("SET application_name = 'perf_concurrent_load_report';")
            await conn.execute("""
                INSERT INTO dms.perf_test_results (
                    test_name, test_type, start_time, end_time,
                    rows_affected, avg_latency_ms, max_latency_ms, min_latency_ms,
                    p95_latency_ms, p99_latency_ms, test_parameters, additional_metrics
                ) VALUES (
                    $1, $2, NOW() - interval '1 minute' * $3, NOW(),
                    $4, $5, $6, $7, $8, $9, $10, $11
                )
            """,
                test_name,
                'concurrent_load',
                int(stats['total_time_seconds'] / 60),  # Approximate start time
                stats['successful_operations'],
                stats['latency_mean_ms'],
                stats['latency_max_ms'],
                stats['latency_min_ms'],
                stats['latency_p95_ms'],
                stats['latency_p99_ms'],
                json.dumps({'sessions': self.pool_size, 'operations_per_session': stats['total_operations'] // self.pool_size}),
                json.dumps(stats)
            )

    async def cleanup(self):
        """Clean up resources"""
        if self.pool:
            await self.pool.close()

async def main():
    parser = argparse.ArgumentParser(description='DMS Reference Table Load Tester')
    parser.add_argument('--sessions', type=int, default=10, help='Number of concurrent sessions')
    parser.add_argument('--iterations', type=int, default=100, help='Operations per session')
    parser.add_argument('--test-name', default=f'load_test_{datetime.now().strftime("%Y%m%d_%H%M%S")}',
                        help='Name for the test run')

    args = parser.parse_args()

    tester = LoadTester(pool_size=args.sessions)

    try:
        # Initialize
        print("Initializing connection pool...")
        await tester.init_pool()
        await tester.load_test_data()

        # Run test
        stats, latencies = await tester.run_concurrent_test(args.sessions, args.iterations)

        if stats:
            # Print results
            print("\n" + "="*60)
            print("LOAD TEST RESULTS")
            print("="*60)
            print(f"Total Operations: {stats['total_operations']}")
            print(f"Successful: {stats['successful_operations']}")
            print(f"Failed: {stats['failed_operations']}")
            print(f"Duration: {stats['total_time_seconds']:.2f} seconds")
            print(f"Throughput: {stats['operations_per_second']:.2f} ops/sec")
            print("\nLatency Statistics (ms):")
            print(f"  Min: {stats['latency_min_ms']:.2f}")
            print(f"  Mean: {stats['latency_mean_ms']:.2f}")
            print(f"  Median: {stats['latency_median_ms']:.2f}")
            print(f"  Max: {stats['latency_max_ms']:.2f}")
            print(f"  StdDev: {stats['latency_stdev_ms']:.2f}")
            print(f"  P95: {stats['latency_p95_ms']:.2f}")
            print(f"  P99: {stats['latency_p99_ms']:.2f}")

            # Save to database
            await tester.save_results(args.test_name, stats, latencies)
            print(f"\nResults saved to database with test name: {args.test_name}")

    except Exception as e:
        print(f"Error during test: {e}")
        sys.exit(1)
    finally:
        await tester.cleanup()

if __name__ == "__main__":
    asyncio.run(main())
