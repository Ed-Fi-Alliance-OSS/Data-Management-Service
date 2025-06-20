# Claude Code Reference for Ed-Fi Data Management Service

## Project Overview

This repository contains the **Ed-Fi Data Management Service (DMS) Platform**, which consists of two main applications:

1. **Ed-Fi Data Management Service (DMS)** - A functional implementation of Ed-Fi Resources API, Ed-Fi Descriptors API, and Ed-Fi Discovery API
2. **Ed-Fi DMS Configuration Service** - A functional implementation of the Ed-Fi Management API specification

These applications are being built to replace the legacy Ed-Fi ODS/API and Ed-Fi ODS Admin API applications,
with a target production-ready 1.0 release in Q4 2025. The project is developed under the code name "Project Tanager".

## Technology Stack

- **.NET 8.0** - Primary development framework (C#)
- **ASP.NET Core** - Web API framework
- **PostgreSQL** - Primary OLTP datastore
- **OpenSearch/Elasticsearch** - Search and query capabilities
- **Keycloak** - Identity provider and OAuth 2.0 token management
- **Kafka** - Change data capture (CDC) and event streaming
- **Docker** - Containerization and local development
- **PowerShell** - Build automation and scripting
- **NUnit** - Unit and integration testing
- **Playwright** - End-to-end testing

## Project Structure

```
Data-Management-Service/
├── src/
│   ├── dms/                          # Main Data Management Service
│   │   ├── frontend/                 # AspNetCore web API frontend
│   │   ├── backend/                  # Database backends (PostgreSQL, MSSQL, OpenSearch)
│   │   ├── core/                     # Core business logic and services
│   │   ├── clis/                     # Command-line tools (ApiSchemaDownloader, etc.)
│   │   └── tests/                    # E2E tests
│   └── config/                       # DMS Configuration Service
│       ├── frontend/                 # AspNetCore web API frontend
│       ├── backend/                  # Database backends and identity providers
│       ├── datamodel/                # Shared data models
│       └── tests/                    # E2E tests
├── eng/                              # Engineering tools and scripts
│   ├── docker-compose/               # Docker composition files and startup scripts
│   ├── bulkLoad/                     # Performance testing and bulk loading
│   └── smoke_test/                   # Smoke testing scripts
├── docs/                             # Developer documentation
└── build scripts (*.ps1)            # PowerShell build automation
```

## Key Build Commands

### Main Build Scripts
- `./build-dms.ps1` - Build script for Data Management Service
- `./build-config.ps1` - Build script for Configuration Service
- `./setup-dev-environment.ps1` - Sets up development environment

### Common Commands

#### Development Setup
```powershell
# Setup development environment (run once after clone)
./setup-dev-environment.ps1

# Build both services
./build-dms.ps1 build
./build-config.ps1 build

# Run services locally (requires PostgreSQL)
./build-dms.ps1 run
./build-config.ps1 run
```

#### Testing
```powershell
# Unit tests
./build-dms.ps1 unittest
./build-config.ps1 unittest

# Integration tests (requires database)
./build-dms.ps1 integrationtest
./build-config.ps1 integrationtest

# End-to-end tests (requires Docker)
./build-dms.ps1 e2etest
./build-config.ps1 e2etest

# Generate coverage report
./build-dms.ps1 coverage
```

#### Docker Operations
```powershell
# Build Docker images
./build-dms.ps1 dockerbuild
./build-config.ps1 dockerbuild

# Run in Docker
./build-dms.ps1 dockerrun
./build-config.ps1 dockerrun
```

#### Publishing and Packaging
```powershell
# Build and publish
./build-dms.ps1 buildandpublish
./build-config.ps1 buildandpublish

# Create NuGet packages
./build-dms.ps1 package
./build-config.ps1 package
```

### Docker Compose Commands

Located in `eng/docker-compose/`:

```powershell
# Start full local development environment with all services
./start-local-dms.ps1 -EnableConfig -EnableSearchEngineUI

# Start with specific search engine
./start-local-dms.ps1 -EnableConfig -SearchEngine ElasticSearch

# Stop services
./start-local-dms.ps1 -EnableConfig -d

# Stop and remove volumes (clean slate)
./start-local-dms.ps1 -EnableConfig -d -v

# Start individual services
./start-postgresql.ps1
./start-keycloak.ps1
```

## Development Workflow

### First-Time Setup
1. Clone the repository
2. Run `./setup-dev-environment.ps1` to install .NET tools and Husky
3. Copy `eng/docker-compose/.env.example` to `eng/docker-compose/.env`
4. Start services: `cd eng/docker-compose && ./start-local-dms.ps1 -EnableConfig`

### Local Development
1. Start PostgreSQL and supporting services via Docker Compose
2. Build: `./build-dms.ps1 build`
3. Run locally: `./build-dms.ps1 run` (listens on http://localhost:8080)
4. Use `getting-started.http` for testing HTTP endpoints

### Testing Strategy
- **Unit Tests**: Fast, no external dependencies
- **Integration Tests**: Database required, test data access layer
- **E2E Tests**: Full Docker environment, test complete workflows

## Configuration

### Key Configuration Files
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json`
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.json`
- `eng/docker-compose/.env` - Environment variables for Docker

### Important Settings
- **Datastore**: `postgresql` or `mssql`
- **QueryHandler**: `postgresql` or `opensearch`
- **DeployDatabaseOnStartup**: Auto-create database on startup
- **ConnectionStrings**: Database and OpenSearch connections
- **RateLimit**: Optional API rate limiting configuration

## Architecture Notes

### Data Flow
1. **OLTP Operations**: Direct PostgreSQL access for CRUD operations
2. **Query Operations**: Uses OpenSearch/Elasticsearch for "GET all" and search
3. **CDC Pipeline**: Debezium → Kafka → OpenSearch connector for real-time replication
4. **Authentication**: Keycloak-based OAuth 2.0 with JWT tokens

### Key Components
- **Frontend.AspNetCore**: Web API controllers and middleware
- **Core**: Business logic, API schema validation, pipeline processing
- **Backend.Postgresql**: Data access layer and SQL operations
- **Backend.OpenSearch**: Search query handling
- **Backend.Installer**: Database deployment and migration

## Development Tools

### .NET Tools (configured in `.config/dotnet-tools.json`)
- **CSharpier**: Code formatting (version 0.30.6)
- **Husky**: Git hooks management (version 0.7.2)

### Code Quality
- **SonarAnalyzer.CSharp**: Static code analysis
- **Coverlet**: Code coverage analysis (58% threshold)
- **TreatWarningsAsErrors**: Enforced in builds
- **Pre-commit hooks**: Automatic code formatting via Husky

### Testing Tools
- **NUnit**: Unit testing framework
- **Playwright**: Browser automation for E2E tests
- **FakeItEasy**: Mocking framework
- **FluentAssertions**: Assertion library
- **Testcontainers**: Containerized integration testing

## Important Files for Development

### Build and Configuration
- `/build-dms.ps1`, `/build-config.ps1` - Main build scripts
- `/setup-dev-environment.ps1` - Environment setup
- `src/Directory.Packages.props` - Centralized package management
- `.config/dotnet-tools.json` - Development tools

### Documentation
- `/GETTING_STARTED.md` - Lab walkthrough and tutorial
- `/docs/RUNNING-LOCALLY.md` - Local development guide
- `/docs/CONFIGURATION.md` - Configuration reference
- `/docs/SETUP-DEV-ENVIRONMENT.md` - Development environment setup
- `/getting-started.http` - Sample HTTP requests

### Docker and Deployment
- `eng/docker-compose/` - All Docker composition files
- `src/dms/Dockerfile`, `src/config/Dockerfile` - Container definitions
- `src/dms/run.sh`, `src/config/run.sh` - Container startup scripts

## Git and Development Practices

- **Main branch**: `main`
- **Pre-commit hooks**: Automatic CSharpier formatting
- **Code coverage**: 58% minimum threshold enforced

## External Dependencies

- Ed-Fi Data Standard packages (ApiSchema)
- Authentication via Keycloak
- PostgreSQL for primary storage
- OpenSearch/Elasticsearch for querying
- Kafka for change data capture
- Docker for local development environment

This project follows enterprise .NET development practices with comprehensive testing,
containerization, and modern DevOps workflows.
