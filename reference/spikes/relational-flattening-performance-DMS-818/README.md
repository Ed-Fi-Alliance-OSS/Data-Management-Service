# Ed-Fi Relational Flattening Documentation

This directory contains comprehensive documentation for the Ed-Fi Data Management Service relational flattening proof-of-concept, which explores the performance implications of natural keys versus surrogate keys in PostgreSQL implementations.

## Documentation Index

### Executive Summary
- [**PERFORMANCE-TESTING-OUTCOMES.md**](./PERFORMANCE-TESTING-OUTCOMES.md) - Executive summary consolidating all performance testing findings, emphasizing that query optimization dominates schema design choices for ETL workloads.

### Technical Implementation
- [**INITIAL_TESTING_APPROACH.md**](./INITIAL_TESTING_APPROACH.md) - Comprehensive technical documentation of the ETL transformation journey from SQL Server to PostgreSQL, including five implementation approaches and design patterns.

- [**TABLES_USED_BY_QUERY.md**](./TABLES_USED_BY_QUERY.md) - Complete reference of all Ed-Fi database tables, their primary keys, and usage patterns within the ETL transformation functions.

### Performance Analysis
- [**OPTIMIZATION-COMPARISON.md**](./OPTIMIZATION-COMPARISON.md) - Detailed performance analysis report comparing six query variants with comprehensive statistical analysis, run-by-run metrics, and optimization recommendations.

- [**SIMPLIFIED-QUERY-PERFORMANCE-COMPARISON.md**](./SIMPLIFIED-QUERY-PERFORMANCE-COMPARISON.md) - Performance analysis of simplified test queries that isolate core functionality, revealing that surrogate key joins can outperform natural keys when complexity is reduced.

### Getting Started
- [**HOW-TO-RUN-PERFORMANCE-TESTS.md**](./HOW-TO-RUN-PERFORMANCE-TESTS.md) - Step-by-step guide for setting up the test environment, deploying databases, and running all performance test suites.



## Key Findings

1. **Query optimization provides 1.88x to 2.87x performance improvements** regardless of key strategy
3. **Compatibility views add overhead** (12% slower in full implementation)
4. **SQL query optimization more important by an order of magnitude** for ETL workload performance

## Test Environment

- **Database**: PostgreSQL 13 (Docker containers)
- **Dataset**: Northridge test data (~21,600 students)
- **Testing**: 50-300 query executions per variant
- **Metrics**: Execution time, row counts, standard deviation, consistency

For detailed setup instructions, see [HOW-TO-RUN-PERFORMANCE-TESTS.md](./HOW-TO-RUN-PERFORMANCE-TESTS.md).
