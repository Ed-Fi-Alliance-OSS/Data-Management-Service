# Data-Management-Service

Code and design documentation for the Ed-Fi Data Management Service, successor
to the Ed-Fi ODS/API.

<img alt="Scarlet Tanager, by Adam Jackson, no rights reserved"
src="https://raw.githubusercontent.com/Ed-Fi-Alliance-OSS/Project-Tanager/main/images/scarlet-tanager_by_adam-jackson_no-rights-reserved_square-256.png"
align="right" style="padding: 0 0 1rem 1rem; max-width: 25%"> This application
is part of [Project
Tanager](https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager), an ambitious
project to build a cloud-native "next generation" of
[Ed-Fi](https://www.ed-fi.org) software. The Ed-Fi Data Management Service (DMS)
will replace [Ed-Fi ODS/API 7.x](https://techdocs.ed-fi.org/x/UQSUCg).

Developer Documents:

* See [Project Tanager](https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager)
  for design documents and discussion.
* [Code of Conduct](./CODE_OF_CONDUCT.md)
* [List of Contributors](./CONTRIBUTORS.md)
* [Copyright and License Notices](./NOTICES.md)
* [License](./LICENSE)

## Configuration

### RateLimit

Basic rate limiting can be applied by supplying a `RateLimit` object in the
`appsettings.json` file. If no `RateLimit` object is supplied, rate limiting is
not configured for the application. Rate limiting (when applied) will be set
globally and apply to all application endpoints. 

The `RateLimit` object should have the following parameters.

Parameter       | Description
----------------|----------------
`PermitLimit`   | The number of requests permitted within the window time span. This will be the number of requests, per hostname permitted per timeframe (`Window`). Must be > 0.
`Window`        | The number of seconds before the `PermitLimit` is reset. Must be > 0. 
`QueueLimit`    | The maximum number of requests that can be Queued once `PermitLimit`s are exhausted. These requests will wait until the `Window` expires and will be processed FIFO. When the queue is exhausted, clients will receive a `429` `Too Many Requests` response. Must be >= 0. 


## Legal Information

Copyright (c) 2024 Ed-Fi Alliance, LLC and contributors.

Licensed under the [Apache License, Version 2.0](./LICENSE) (the "License").

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.
