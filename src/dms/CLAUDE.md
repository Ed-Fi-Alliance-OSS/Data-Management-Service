# Claude Code Reference for Ed-Fi Data Management Service (DMS)

## Project Overview

The **Ed-Fi Data Management Service (DMS)** is a modern .NET 8.0-based platform that implements the Ed-Fi Alliance's
educational data standards. This is the core service component of the larger Ed-Fi Data Management Service Platform,
designed to replace legacy Ed-Fi ODS/API systems with a more scalable architecture. The
project is developed under code name "Project Tanager" with a target production release of 1.0 in Q4 2025.

**Key Purpose**: Provides a functional implementation of:
- Ed-Fi Resources API (CRUD operations for educational entities)
- Ed-Fi Descriptors API (enumeration and lookup data)
- Ed-Fi Discovery API (metadata and versioning)

## Technology Stack

### Core Technologies
- **.NET 8.0** - Primary development framework (C#)
- **ASP.NET Core** - Web API framework with JWT Bearer authentication
- **PostgreSQL** - Primary OLTP datastore (with optional SQL Server support)
- **OpenSearch/Elasticsearch** - Search and query capabilities for read operations
- **Docker** - Containerization and deployment

### Key Dependencies
- **Serilog** - Structured logging with file and console sinks
- **Polly** - Resilience patterns (circuit breaker, retry policies)
- **Dapper** - Micro ORM for efficient database operations
- **JsonSchema.Net** - JSON schema validation for Ed-Fi API schemas
- **Keycloak.Net.Core** - OAuth 2.0/OpenID Connect integration
- **FluentValidation** - Object validation framework
- **NUnit** - Unit and integration testing
- **Playwright** - End-to-end testing with browser automation

## Project Structure

The codebase follows a clean, layered architecture organized into:

### 1. Core Layer (`/core/`)
```
EdFi.DataManagementService.Core/           # Business logic and pipeline processing
EdFi.DataManagementService.Core.External/  # Interface definitions for loose coupling
```

**Key Components:**
- **Pipeline Architecture**: Request processing through configurable middleware pipeline
- **API Schema Management**: Dynamic loading and validation of Ed-Fi API schemas
- **Security**: Claim-based authorization with education organization hierarchy
- **Resource Management**: Dependency resolution and bulk operation optimization

### 2. Frontend Layer (`/frontend/`)
```
EdFi.DataManagementService.Frontend.AspNetCore/      # Main web API application
EdFi.DataManagementService.Frontend.SchoolYearLoader/ # School year data utility
```

**Features:**
- RESTful API endpoints following Ed-Fi Resource API specification
- Health checks, rate limiting, authentication middleware
- Discovery API endpoints for metadata and version information
- Configurable path base and CORS support

### 3. Backend Layer (`/backend/`)
```
EdFi.DataManagementService.Backend.Postgresql/  # PostgreSQL data access layer
EdFi.DataManagementService.Backend.OpenSearch/  # Search query handling
EdFi.DataManagementService.Backend.Mssql/       # SQL Server support (alternative)
EdFi.DataManagementService.Backend.Installer/   # Database deployment utility
```

**Architecture Pattern:**
- CQRS-like separation: writes to PostgreSQL, reads can use OpenSearch
- Repository pattern with interface abstraction
- Database partitioning and optimistic locking support

### 4. Command Line Tools (`/clis/`)
```
ApiSchemaDownloader/  # Downloads Ed-Fi API schema packages from NuGet
OpenApiGenerator/     # Generates OpenAPI specifications from schemas
```

### 5. Testing (`/tests/`)
```
EdFi.DataManagementService.Tests.E2E/  # End-to-end tests using Reqnroll (BDD)
RestClient/                             # HTTP test files for manual API testing
```

## Key Build Commands

### Main Build Script: `./build-dms.ps1`

**Available Commands:**
```powershell
# Build and compile
./build-dms.ps1 build                    # Clean, restore, build
./build-dms.ps1 buildandpublish         # Build and publish for deployment

# Testing
./build-dms.ps1 unittest                # Run unit tests (no database required)
./build-dms.ps1 integrationtest         # Run integration tests (database required)
./build-dms.ps1 e2etest                 # Run end-to-end tests (Docker required)
./build-dms.ps1 coverage                # Generate code coverage report

# Docker Operations
./build-dms.ps1 dockerbuild             # Build Docker image
./build-dms.ps1 dockerrun               # Run Docker container

# Local Development
./build-dms.ps1 run                     # Start application locally (http://localhost:8080)

# Packaging
./build-dms.ps1 package                 # Create NuGet packages
./build-dms.ps1 push -NuGetApiKey <key> # Publish to NuGet feed
```

### Development Environment Setup

**Prerequisites:**
- .NET 8.0 SDK
- PowerShell Core 7+
- Docker Desktop or Podman
- PostgreSQL (for local development)

**First-Time Setup:**
```powershell
# 1. Setup development environment (installs .NET tools, Husky)
./setup-dev-environment.ps1

# 2. Copy environment configuration
cp eng/docker-compose/.env.example eng/docker-compose/.env

# 3. Start supporting services
cd eng/docker-compose
./start-postgresql.ps1
./start-keycloak.ps1

# 4. Build and run DMS
./build-dms.ps1 build
./build-dms.ps1 run
```

## Configuration

### Key Configuration Files
- `appsettings.json` - Local development settings
- `appsettings.template.json` - Environment variable template for containers
- `run.sh` - Container startup script with database readiness checks

### Important Settings
```json
{
  "Datastore": "postgresql",              // or "mssql"
  "QueryHandler": "postgresql",           // or "opensearch"
  "DeployDatabaseOnStartup": true,        // Auto-create database schema
  "RateLimit": {                          // Optional API rate limiting
    "EnableRateLimiting": false
  }
}
```

## Development Workflow

### Local Development
1. **Start Services**: Use Docker Compose to start PostgreSQL, Keycloak, OpenSearch
2. **Build**: `./build-dms.ps1 build`
3. **Run**: `./build-dms.ps1 run` (listens on http://localhost:8080)
4. **Test**: Use `getting-started.http` or `tests/RestClient/` files for API testing

### Testing Strategy
- **Unit Tests**: Fast, no external dependencies, comprehensive coverage
- **Integration Tests**: Database required, test data access layer
- **E2E Tests**: Full Docker environment, Playwright for browser automation
- **API Tests**: HTTP files for manual endpoint validation

### Docker Operations
```powershell
# Start full development environment
cd eng/docker-compose
./start-local-dms.ps1 -EnableConfig -EnableSearchEngineUI

# Individual services
./start-postgresql.ps1
./start-keycloak.ps1

# Stop and clean
./start-local-dms.ps1 -d -v
```

## Architecture & Key Features

### Data Architecture
- **OLTP Operations**: Direct PostgreSQL access for create, update, delete
- **Query Operations**: OpenSearch/Elasticsearch for "GET all" and search queries
- **Change Data Capture**: Integration points for Kafka-based event streaming
- **Reference Integrity**: Automated validation and cascade operations

### Security & Authorization
- **OAuth 2.0/JWT**: Token-based authentication via Keycloak
- **Claim-based Authorization**: Fine-grained access control
- **Education Organization Hierarchy**: Scoped data access based on organization structure
- **Student/Staff Privacy**: Built-in privacy protection mechanisms

### API Capabilities
- **Ed-Fi Compliant**: Full implementation of Ed-Fi Resource and Descriptor APIs
- **Advanced Querying**: Filtering, pagination, field selection, sorting
- **Bulk Operations**: Optimized for high-volume data operations
- **Schema Validation**: Comprehensive JSON schema validation
- **Discovery**: Metadata endpoints for API introspection

### Operational Features
- **Health Monitoring**: Built-in health checks and readiness probes
- **Rate Limiting**: Configurable per-endpoint rate limiting
- **Resilience**: Circuit breaker patterns, retry policies via Polly
- **Structured Logging**: Correlation IDs, performance metrics
- **Code Quality**: SonarAnalyner integration, 58% code coverage minimum

## Development Tools & Quality

### .NET Tools (`.config/dotnet-tools.json`)
- **CSharpier**: Code formatting (version 0.30.6)
- **Husky**: Git hooks for pre-commit formatting (version 0.7.2)

### Code Quality Standards
- **Warnings as Errors**: Enforced in all builds
- **Central Package Management**: All versions managed in `Directory.Packages.props`
- **Pre-commit Hooks**: Automatic code formatting via Husky
- **Static Analysis**: SonarAnalyzer.CSharp integration

## Educational Domain Focus

This system is specifically designed for K-12 education data management, implementing Ed-Fi Alliance standards for:
- **Student Information Systems**: Demographics, enrollment, grades, attendance
- **Learning Management**: Courses, sections, assignments, assessments
- **Assessment Data**: Test scores, competency tracking, standards alignment
- **Staff Information**: Personnel, assignments, certifications
- **Financial Systems**: Budget, expenditures, payroll integration

## Important Files for Development

### Build and Configuration
- `/build-dms.ps1` - Main build script with all commands
- `/setup-dev-environment.ps1` - Development environment setup
- `Directory.Packages.props` - Centralized NuGet package management
- `.config/dotnet-tools.json` - Development tools configuration

### Testing and API
- `getting-started.http` - Sample HTTP requests for API testing
- `tests/RestClient/` - Additional HTTP test files
- `tests/EdFi.DataManagementService.Tests.E2E/` - End-to-end test specifications

### Docker and Deployment
- `Dockerfile` - Multi-stage container build
- `run.sh` - Container startup script
- `eng/docker-compose/` - Complete Docker development environment

## External Dependencies & Integration

- **Ed-Fi Data Standard**: API schema packages from Ed-Fi Alliance NuGet feed
- **Keycloak**: Identity provider for OAuth 2.0 authentication
- **PostgreSQL/SQL Server**: Primary data storage
- **OpenSearch/Elasticsearch**: Search and query engine
- **Kafka**: Change data capture and event streaming (integration points)

This is an enterprise-grade educational data platform following modern .NET development practices
with comprehensive testing, containerization, and DevOps workflows specifically designed for educational
data privacy and compliance requirements.
