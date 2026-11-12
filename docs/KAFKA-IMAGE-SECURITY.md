# Kafka image security assessment — 2026-11-08

The exact shipped Kafka 4.3.2 image still contains reported vulnerabilities.
**Disposition: remediation required; no security clearance or risk acceptance is
asserted by DMS-1675.** Functional qualification and storage compatibility do not
resolve these package findings. No finding has been suppressed.

## Scan identity and scope

| Input | Recorded value |
| --- | --- |
| Image | `apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7` |
| Platform / OS | Linux/amd64; Alpine 3.24.2 |
| Scanner | Trivy 0.76.2, released 2026-11-05 |
| Scanner archive SHA-256 | `bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf` |
| Vulnerability DB updated | 2026-11-08 18:58:44 UTC, schema 2 |
| Java DB updated | 2026-11-08 00:58:16 UTC, schema 1 |
| Report created | 2026-11-08 22:12:41 UTC |
| JSON report SHA-256 | `35c08086b98eaa72291fb8ee289940ebddb9edc69f7a8e7d30f503bfc8a81242` |

The scanner archive was checked against both the GitHub release asset digest and
its published checksum file. A new cache downloaded both databases. The scan used
`--scanners vuln --image-src docker --platform linux/amd64 --ignorefile /dev/null`,
with no severity filter or `--ignore-unfixed`. The report identifies the selected
digest and amd64 image configuration. It reports **10 package findings across nine
unique CVEs: 1 critical, 7 high, 2 medium, 0 low, 0 unknown**. All ten are Alpine
package findings; the Java archive result reports none. This scope does not cover
secrets, configuration, other platforms, exploit testing, or unknown vulnerabilities.

The ticket's 2026-11-03 result was nine findings: one critical, three high, two
medium and three unknown. The critical Brotli finding and all three previously
named high Expat findings remain. A change in counts for an unchanged digest
reflects the scan/database assessment, not patched image contents. The historical
summary alone is insufficient to reconstruct every per-finding severity change.

## Findings and disposition

Installed and fixed versions below are from Trivy, corroborated against
[Alpine's 3.24 security database](https://secdb.alpinelinux.org/v3.24/main.json).
A `fixed` status in Trivy means a fixed package is available, **not** that the
installed image is fixed. All rows remain open for image remediation; the
reachability qualifications below are not ignore rules or approved exceptions.

| CVE | Package / installed | Severity | Fixed package version | Assessment / disposition |
| --- | --- | --- | --- | --- |
| CVE-2026-111497 | brotli-libs 1.2.0-r1 | Critical | 1.2.1-r0 | Crafted Brotli input can corrupt decoder memory. No broker request path to this native decoder was established; it remains installed through freetype and GnuTLS. Update the image. |
| CVE-2026-113351 | libexpat 2.8.5-r0 | High | 2.10.0-r0 | XML parser reset memory-accounting denial of service. Native Expat is installed for fontconfig; broker reachability was not established. Update the image. |
| CVE-2026-115506 | libexpat 2.8.5-r0 | High | 2.10.0-r0 | Delayed XML entity-amplification protection. Same native-parser exposure qualification; update the image. |
| CVE-2026-117610 | libexpat 2.8.5-r0 | High | 2.10.0-r0 | External-entity parsing null dereference. Same native-parser exposure qualification; update the image. |
| CVE-2026-118370 | libexpat 2.8.5-r0 | High | 2.10.0-r0 | Attribute-count integer overflow and possible memory corruption. Same native-parser exposure qualification; update the image. |
| CVE-2026-77214 | libexpat 2.8.5-r0 | High | 2.9.0-r0 | Parse-buffer heap over-read. Same native-parser exposure qualification; use 2.10.0-r0 or later to cover all reported Expat findings. |
| CVE-2026-102633 | libexpat 2.8.5-r0 | Medium | 2.9.0-r0 | Advisory describes a 32-bit allocation overflow; this scan is amd64. Architecture limits apparent applicability, but no package suppression is made. Covered by the Expat update. |
| CVE-2026-107891 | musl and musl-utils 1.2.6-r2 (two findings) | High each | 1.2.6-r3 | Native regex compilation integer overflow. The JVM uses musl; no broker-controlled path into the affected regex compiler was established. Update both packages through a rebuilt image. |
| CVE-2026-4738 | zlib 1.3.2-r1 | Medium | 1.4-r0 | Advisory references GDAL's contributed `infback9` implementation. Installed zlib lists only `libz.so` files, not GDAL/infback9. Applicability needs upstream clarification; retain the finding and update the package. |

The primary [musl fix](https://git.musl-libc.org/cgit/musl/commit/?id=7813e9119eb5fe681b6750b8a033a0490bca0941)
addresses integer overflow in `regcomp`. The
[GDAL fix](https://github.com/OSGeo/gdal/pull/12244) targets its contributed
`infback9/inftree9.c`, which explains the zlib applicability question; it does not
justify silently overriding Alpine's package match.

Read-only inspection of the exact image confirmed package versions/dependencies
and the JVM's musl linkage. A sampled running test broker's `/proc/1/maps` contained
musl and JDK native libraries, with no mapped Brotli, Expat or system libz library.
This is limited runtime evidence: it does not exclude later dynamic loading,
startup/helper-process exposure or another workload using those packages. No
exploit attempt or exhaustive reachability analysis was performed.

## Remediation boundary

The image owner/release maintainer needs an upstream rebuilt image (or a separately
maintained and qualified derivative) containing the fixed packages, then must pin
its immutable digest and rerun the scan and affected Kafka qualification. This
assessment does not replace the selected image with an unqualified candidate or
run `apk upgrade` inside a deployment; either would produce different bytes from
the digest tested here. Record any formal exception separately with its owner,
scope and expiry if shipment is chosen before remediation. No such exception is
recorded here.

The shipped Compose broker binds its host port to loopback and runs Kafka as UID
1000 after volume initialization. Those controls reduce exposure/privilege; they
do not fix native-library vulnerabilities or establish non-exploitability.

## Reproduce and retain evidence

Download Trivy from its [official release](https://github.com/aquasecurity/trivy/releases/tag/v0.76.2)
and verify the archive digest above. With the pinned Kafka image present locally:

```bash
trivy image --cache-dir /tmp/kafka-security-new-cache \
  --scanners vuln --image-src docker --platform linux/amd64 \
  --ignorefile /dev/null --format json --output /tmp/kafka-security.json \
  apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7
```

Use a new cache/output location per assessment. Retain the full JSON, scanner
version/checksum, DB metadata, exact image identity and assessment date; future
DBs may yield different results for the same image. The local artifact location
and combined evidence fingerprint are in the
[DMS-1675 implementation record](../reference/design/DMS-1675-kafka-upgrade-and-smoke-tests.md).
