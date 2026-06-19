# Ed-Fi API

[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/badge)](https://securityscorecards.dev/viewer/?uri=github.com/Ed-Fi-Alliance-OSS/Data-Management-Service)

This repository contains the **Ed-Fi API**, version 8 — the next major version of
the Ed-Fi API platform, continuing the lineage from the Ed-Fi ODS/API. It comprises
two applications:

1. The Ed-Fi API (the Data Management Service, or "DMS", as the internal codebase
   name), a functional implementation of the Ed-Fi Resources API, Ed-Fi Descriptors
   API, and Ed-Fi Discovery API specifications.
2. The Ed-Fi API Configuration Service (internally, the DMS Configuration Service),
   a functional implementation of the Ed-Fi Management API specification.

These applications replace the legacy Ed-Fi ODS/API and Ed-Fi ODS Admin API
applications.

See [Getting Started](./GETTING_STARTED.md) for a detailed tutorial on starting
the Ed-Fi API and interacting with it.

See the [reference folder](./reference/) for detailed design documents.

See the [docs folder](./docs/) for additional developer-oriented documentation.

## Contributing

The Ed-Fi Alliance welcomes code contributions from the community. Please read
the [Ed-Fi Contribution Guidelines](https://docs.ed-fi.org/community/sdlc/code-contribution-guidelines/)
for detailed information on how to contribute source code.

## Repository Metadata

- [Code of Conduct](./CODE_OF_CONDUCT.md)
- [List of Contributors](./CONTRIBUTORS.md)
- [Copyright and License Notices](./NOTICES.md)
- [Security](./SECURITY.md)
- [License](./LICENSE)

## API Behavior

- [Data Strictness](./docs/DATA-STRICTNESS.md) — case-sensitive request-body
  property names (a breaking change from the ODS/API) and related request-body
  validation rules

## Developer Documents

- [Running in Local Context](./docs/RUNNING-LOCALLY.md)
- [Configuration](./docs/CONFIGURATION.md)
- [Relational Backend Developer Guide](./docs/RELATIONAL-BACKEND.md)
- [Docker](./docs/DOCKER.md)
- [Removing reference validation](./docs/REFERENCE-VALIDATION.md)
- [Setting Up Development Environment](./docs/SETUP-DEV-ENVIRONMENT.md)
- [School Year Loader](./docs/SCHOOL-YEAR-LOADER.md)

## Legal Information

Copyright (c) 2026 Ed-Fi Alliance, LLC and contributors.

Licensed under the [Apache License, Version 2.0](./LICENSE) (the "License").

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.
