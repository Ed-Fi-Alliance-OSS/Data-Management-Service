# Operational Concerns

These notes generally apply to any application in the DMS platform.

## Configuration

See [Configuration](./CONFIGURATION.md)

## Deployment

The DMS software is designed for operation on-premises or using cloud-based
managed services. However, the Ed-Fi Alliance will not necessarily provide
detailed deployment orchestration for various environments.

The applications are built in a container-first fashion. A Kubernetes topology,
and potentially Docker Compose topology, is provided for basic testing and
demonstration purposes. These artifacts might be useful for production
deployments into Kubernetes. Anyone using them as such should review carefully,
particularly with respect to security concerns.

Although the application testing process will focus on the container-based
integration, these applications should be able to run on "bare metal" (or
virtual machine) without a container.

## Plugins

A plugin is a directory of already-published assemblies that the Ed-Fi API loads at
startup and lets contribute to its service composition. DMS ships no fetcher:
getting a plugin directory under the plugin root is a **deployment** step that
happens before the process starts, and it comes in two recipes, below.

Acquiring the bytes does not enable them. A plugin runs if and only if its directory
name appears in `Plugins:Allowed`, which is a separate, deployment-owned setting
neither recipe writes. See [Plugins](./CONFIGURATION.md#plugins) for that setting and
for configuration precedence, and
[PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md) for what an implementer has
to do on the other side.

### Trust

**A loaded plugin runs with full process trust.** It can read every connection
string, every decrypted secret, every request body, and every token. .NET has no
code-access security, does not verify strong names when loading an assembly from a
path, and does not check Authenticode signatures at load on any platform. The
isolated load context each plugin gets isolates **assembly identity**; it is **not**
a security boundary. Every control below is about the integrity of what gets loaded,
not about containing it once loaded.

**Write access to the plugin root is equivalent to code execution as the host
process.** That sentence is the threat model. Because DMS ships no fetcher, the
posture is the same in every deployment: **the runtime identity never writes the
plugin root.** The plugin root must be writable by the deployment identity and not
by the runtime identity, and the `:ro` mount both recipes use is how a container
deployment states that. **DMS cannot verify it and does not claim to.**

The package digest in Recipe 2 is the second control, and it sits where the download
happens rather than inside DMS, so the bytes that reach the plugin root are bytes the
operator approved whether or not DMS was ever involved. A feed that serves its own
package hashes does not substitute for it: the point of pinning is that the
**operator** approved these bytes, not that the feed vouches for them.

What this does **not** protect against, stated so it is not rediscovered:

- A supply-chain compromise of a package the operator deliberately pinned. The digest
  proves the bytes did not change; it says nothing about what they do.
- An operator with write access to both the plugin root and the configuration, who
  updates a directory and its digest together.
- Anything a plugin does after it loads. There is no capability restriction, no
  resource limit, and no audit beyond the load inventory in the log.
- A plugin that hangs. The contribution hook is synchronous and unbounded, so a
  plugin that blocks blocks startup. That is deliberate — a timeout would mean
  continuing without a plugin the operator required, which the failure rules below
  refuse. What you get instead is attribution: the loader announces each plugin
  before entering its hook, so the last line written names the plugin that hung.

### Plugin names are case-sensitive, on every filesystem

Every comparison the loader makes between an allowlist entry, a directory name, an
assembly name, and the plugin's own declared name is **ordinal**. The released image
is Linux and its filesystem is case-sensitive, so `Acme.Dms.Identity` and
`acme.dms.identity` are two different directories there.

**A case-insensitive filesystem does not soften that.** The loader never asks whether
a path exists, because a Windows or default macOS volume answers yes to that question
for a name that is not the name it was asked about. It lists the parent directory and
compares the spelling it reads back, ordinally, and it does so for the plugin
directory, the entry assembly, and the dependency manifest alike. A mis-cased name is
the same named startup failure on a developer machine as in the image, so it is
caught where the mistake is made rather than at deployment. Write the name exactly as
the directory is named.

The one deliberate exception is the duplicate check over `Plugins:Allowed`, which
folds case: two entries differing only in case are one directory on a
case-insensitive filesystem and two on the image's, so the allowlist is treated as
ambiguous and startup fails.

### The two recipes are alternatives, not additions

Both recipes end at the single `/app/plugins` mount target, and two Compose overlays
cannot both claim it. **Pick one per DMS service.** Each is a Compose overlay added
with its own `-f`; the base Compose files are unchanged by either, which is why a
deployment that uses no plugin materialises no plugin directory at all.

Everything a deployment has to supply is an environment variable declared with `:?`,
so the committed files run as they stand and a second deployment does not have to
edit them.

### Recipe 1: a pre-populated plugin root

The operator publishes plugin directories on the host and bind-mounts the tree
read-only. `DMS_PLUGINS_MOUNT_SOURCE` is the one required value: the host path that
holds the plugin directories.

<!-- embed: eng/docker-compose/plugins-dms.yml -->
```yaml
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Plugin acquisition, recipe 1: a pre-populated plugin root, bind-mounted read-only.
#
# Add with its own -f. It is an alternative to plugins-fetch-dms.yml rather than an addition
# to it: /app/plugins is one mount target and two overlays cannot both claim it.
#
# DMS_PLUGINS_MOUNT_SOURCE is declared with :? rather than a default. A deployment that
# composed this file in meant to supply a host path, and a silent default would make Docker
# materialize an empty root-owned directory beside it, which is exactly what keeping the
# mount out of the base files avoids.
#
# Acquiring the bytes does not enable them. Nothing here writes Plugins:Allowed; the
# allowlist is a separate deployment-owned setting. See eng/docker-compose/README.md,
# "Loading plugins".
services:
  dms:
    volumes:
      - ${DMS_PLUGINS_MOUNT_SOURCE:?set the host path holding the plugin directories}:/app/plugins:ro
```

Nothing in this recipe writes into the mounted directory. Replacing a plugin's
contents is the operator's own step, and the stop-fetch-start rule below applies to
it for the same reason it applies to Recipe 2.

### Recipe 2: a pinned package fetched by the deployment

A deployment that would rather pull a published package than provision a host volume
does so in a one-shot service that runs to completion before DMS starts. It downloads
the `.nupkg`, verifies it against a digest the operator pinned, clears the target
directory, extracts the plugin directory into a named volume, and exits. DMS then
mounts that volume read-only exactly as in Recipe 1.

The three required values are `PLUGIN_PACKAGE_URL`, `PLUGIN_PACKAGE_SHA256`, and
`PLUGIN_NAME`.

<!-- embed: eng/docker-compose/plugins-fetch-dms.yml -->
```yaml
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Plugin acquisition, recipe 2: a pinned package fetched, digest-verified and extracted by a
# one-shot service that runs to completion before DMS starts.
#
# Add with its own -f. It is an alternative to plugins-dms.yml rather than an addition to it:
# /app/plugins is one mount target and two overlays cannot both claim it.
#
# All three deployment-supplied values are declared with :? rather than defaults. The package
# address, its SHA-256 and the plugin directory name inside it are facts about one operator's
# plugin, and a committed file carrying any of them as a literal could not be run by a second
# deployment without being edited.
#
# Acquiring the bytes does not enable them. Nothing here writes Plugins:Allowed; the allowlist
# is a separate deployment-owned setting. See eng/docker-compose/README.md, "Loading plugins".
#
# Repeat deployments are stop, fetch, start. service_completed_successfully orders container
# startup and nothing more: it does not keep a completed one-shot service away from the files of a
# container already running. Composing this file again over a stack that is up starts the fetcher
# while DMS keeps running on the same volume, and the fetcher clears the plugin directory before it
# extracts. Because assemblies load lazily, a request arriving afterwards can find a declared file
# missing or load a dependency from a version other than the one the startup inventory recorded.
# Take the stack down before fetching again. See the same README section.
services:
  fetch-plugins:
    # Pinned by digest, as every third-party image in this directory already is. An operator may
    # re-pin to an image they have approved; a mutable tag would leave the step that verifies the
    # package digest running on bytes nobody pinned. This one is alpine:3.22, carrying BusyBox
    # 1.37.0, whose wget, sha256sum and unzip are the four commands below.
    image: alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce
    environment:
      PLUGIN_PACKAGE_URL: ${PLUGIN_PACKAGE_URL:?set the .nupkg download address}
      PLUGIN_PACKAGE_SHA256: ${PLUGIN_PACKAGE_SHA256:?set the SHA-256 of the pinned .nupkg}
      PLUGIN_NAME: ${PLUGIN_NAME:?set the plugin directory name inside the package}
    volumes:
      - plugins:/out
    # PLUGIN_NAME is checked before it reaches rm or mv, and the check is the same single-path-
    # segment rule the loader applies to an allowlist entry. Without it a name of ".." would make
    # this recipe delete the volume root. Both destructive commands stay inside /out, which is the
    # named volume below.
    #
    # Clearing the directory before extracting is part of the recipe rather than tidiness: it is
    # what makes the digest a statement about what is on disk rather than only about what was
    # downloaded. Without it, changing a pinned version leaves files from the previous version
    # beside the new ones, covered by no digest and named by no .deps.json.
    #
    # A PLUGIN_NAME matching nothing leaves unzip exiting zero over an empty directory, and the
    # chained mv is what fails the service loudly under sh -ec.
    command: >
      sh -ec '
      case "$$PLUGIN_NAME" in "" | "." | ".." | [!A-Za-z0-9]* | *[!A-Za-z0-9._-]*) echo "PLUGIN_NAME must be a single path segment matching [A-Za-z0-9][A-Za-z0-9._-]*" >&2; exit 2;; esac;
      wget -qO /tmp/plugin.nupkg "$$PLUGIN_PACKAGE_URL" &&
      echo "$$PLUGIN_PACKAGE_SHA256  /tmp/plugin.nupkg" | sha256sum -c - &&
      rm -rf "/out/$$PLUGIN_NAME" &&
      unzip -q /tmp/plugin.nupkg "contentFiles/any/any/$$PLUGIN_NAME/*" -d /tmp/x &&
      mv "/tmp/x/contentFiles/any/any/$$PLUGIN_NAME" /out/'
    logging:
      driver: json-file
      options:
        max-size: "${DOCKER_LOG_MAX_SIZE:-50m}"
        max-file: "${DOCKER_LOG_MAX_FILE:-5}"
  dms:
    depends_on:
      fetch-plugins:
        condition: service_completed_successfully
    volumes:
      - plugins:/app/plugins:ro
volumes:
  plugins:
```

**`PLUGIN_PACKAGE_URL`** is the package's download address in the feed's
`PackageBaseAddress` form, `<base>/<id>/<version>/<id>.<version>.nupkg` in lower
case, where `<base>` is what the feed's `index.json` publishes for that resource. It
differs between feed hosts, which is one more reason the address belongs to the
deployment rather than to DMS. Feed credentials, proxies, and mirrors are the
deployment's business, so a private feed works with the same secret-mounting
facilities the deployment already uses.

**`PLUGIN_PACKAGE_SHA256`** is a SHA-256 over the `.nupkg` **bytes**, which is what
`sha256sum -c` verifies before anything is extracted:

```shell
shasum -a 256 acme.dms.identity.1.2.0.nupkg
```

The package is digested rather than the extracted directory because a `.nupkg` is a
single file with a natural digest that an operator computes in one command and a
build system emits for free. Digesting a directory instead would mean specifying a
content-manifest algorithm — path ordering, separator normalisation, symbolic links,
case-insensitive filesystems — that every operator and every build tool would have to
reimplement identically.

**Clearing `<PluginRoot>/<Name>/` before extracting is part of the recipe rather than
tidiness.** It is what makes the digest a statement about what is **on disk** rather
than only about what was downloaded. Without it, changing a pinned version leaves
files from the previous version beside the new ones, covered by no digest and named
by no `.deps.json`, and the next person to read that directory cannot tell which
version they are looking at.

**The `fetch-plugins` image is pinned by digest**, following the convention every
third-party image in `eng/docker-compose/` already uses — `postgresql.yml` pins its
Postgres image the same way. The committed digest is a real, pullable `alpine` 3.22
image, so the recipe is runnable as it stands rather than a placeholder that must be
replaced before it works. An operator may re-pin it to an image they have approved; a
mutable tag would leave the step that verifies the package digest running on bytes
nobody pinned.

#### Re-fetching is stop, fetch, start

**Take the stack down before fetching again.** `depends_on` with
`condition: service_completed_successfully` orders container **startup**: it holds
DMS back until the one-shot service has exited zero, and it says nothing about that
service running again beside a container that is already up. Composing the deployment
again over a live stack does exactly that, because Compose recreates a service whose
definition changed and leaves an unchanged one running — and the fetcher clears the
plugin directory before it extracts into it.

The volume is mounted read-only into DMS, which stops DMS writing it and does nothing
about a writer outside the container. Assemblies load lazily, so the process does not
hold every declared file open: a request arriving during or after the swap can meet a
missing file, or load a dependency from a version other than the one the startup
inventory recorded — and that inventory is the one record tying a running process to
the bytes it is serving. Nothing inside a Compose file can prevent this, so the
requirement lives in the recipe instead.

Recipe 1 has no fetch step, but the same rule applies to replacing the contents of
its mounted directory, for the same reason.

### Recipe 2 on Kubernetes

The same four commands run in an `initContainers` entry that writes to an `emptyDir`
the DMS container mounts `readOnly: true`. The `PLUGIN_NAME` check is the same
single-path-segment rule the loader applies to an allowlist entry, and it runs before
`rm` and `mv` so that a name of `..` cannot make the recipe delete the volume root.

```yaml
spec:
  volumes:
    - name: plugins
      emptyDir: {}
  initContainers:
    - name: fetch-plugins
      # Pinned by digest for the same reason the Compose recipe pins it.
      image: alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce
      env:
        # Supply these from a ConfigMap, or from a Secret when the feed address
        # carries credentials. They are the deployment's own values, exactly as the
        # three :?-required variables are in the Compose recipe.
        - name: PLUGIN_PACKAGE_URL
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginPackageUrl }
        - name: PLUGIN_PACKAGE_SHA256
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginPackageSha256 }
        - name: PLUGIN_NAME
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginName }
      volumeMounts:
        - name: plugins
          mountPath: /out
      command: ["sh", "-ec"]
      args:
        - |
          case "$PLUGIN_NAME" in "" | "." | ".." | [!A-Za-z0-9]* | *[!A-Za-z0-9._-]*)
            echo "PLUGIN_NAME must be a single path segment matching [A-Za-z0-9][A-Za-z0-9._-]*" >&2
            exit 2
            ;;
          esac
          wget -qO /tmp/plugin.nupkg "$PLUGIN_PACKAGE_URL"
          echo "$PLUGIN_PACKAGE_SHA256  /tmp/plugin.nupkg" | sha256sum -c -
          rm -rf "/out/$PLUGIN_NAME"
          unzip -q /tmp/plugin.nupkg "contentFiles/any/any/$PLUGIN_NAME/*" -d /tmp/x
          mv "/tmp/x/contentFiles/any/any/$PLUGIN_NAME" /out/
  containers:
    - name: dms
      image: edfialliance/ed-fi-api@sha256:<the digest you pinned>
      volumeMounts:
        - name: plugins
          mountPath: /app/plugins
          # The runtime identity never writes the plugin root.
          readOnly: true
```

`sh -ec` is what makes this fail loudly: `-e` stops at the first command that fails,
so a digest mismatch never reaches the extraction. A `PLUGIN_NAME` that matches
nothing inside the package leaves `unzip` exiting zero over an empty directory, and
the `mv` is the command that then fails.

This form is not asserted against a committed file, because no committed file is its
artifact. The Compose blocks above are; see the note at the end of this chapter.

### When a plugin does not load, DMS does not start

**An allowlisted plugin that does not load is fatal.** Skip-and-continue is right
when a missing plugin degrades a capability whose absence the operator can see, and
for these plugins the absence is invisible: a validation plugin that silently did not
load means business rules stop being enforced while writes keep succeeding, and
nothing surfaces until the data is already wrong.

Two outcomes are warnings rather than failures, and a boot produces **at most one**
of them. Both come from the same step, which runs only once every allowlisted plugin
has already loaded, and neither can happen when `Plugins:Allowed` is empty: nothing
was asked for, so the root is never listed at all.

- **Directories under the plugin root that the allowlist does not name.** One
  aggregate warning, not one per directory: a single line listing every ignored
  name, ordinally sorted and comma-separated. Such a directory is never opened,
  never probed, and never has its metadata read. Replayed through the application
  logger as `Plugin loader warning UnallowlistedDirectories`, carrying the names.
- **The plugin root could not be listed.** When enumerating the root fails with an
  I/O or permission error, whether it holds unallowlisted directories is unknown.
  That is not worth failing a boot whose plugins all loaded, so it warns instead.
  Replayed as `Plugin loader warning PluginRootNotListed`, carrying the error
  message and no directory names.

The second is why the absence of the first proves nothing. An operator who greps for
ignored directories and finds none has to rule out `PluginRootNotListed` before
concluding there were none to ignore.

The tables below group every failure by **what you do about it**.

#### Fix the allowlist

| Condition | What the failure says |
| --- | --- |
| A name repeated in `Plugins:Allowed`, before or after trimming, or differing only in case | The allowlist is ambiguous about what was approved. Names the entry. |
| A name that is not a single path segment: rooted, containing `/` or `\`, equal to `.` or `..`, or otherwise outside `[A-Za-z0-9][A-Za-z0-9._-]*` | Names the entry and the rule. Checked before the path is composed, because a rooted name would discard the plugin root entirely. |
| A composed plugin path that does not sit under the plugin root once symbolic links are resolved | Names the entry and both resolved paths. |
| An allowlisted directory, or its entry assembly, is missing | Names the expected path. Check the spelling first; the other cause is a directory the deployment did not deliver. |

#### Fix the deployment

| Condition | What the failure says |
| --- | --- |
| The plugin root is missing and `Plugins:Allowed` is non-empty | Plugins were asked for and cannot be delivered. Mount the root, or empty the allowlist. |
| The entry assembly is present but fails to load | Reports the load exception. The bytes that reached the root are not the bytes that were meant to. |

A plugin root that is missing while `Plugins:Allowed` is **empty or absent** is not
an error at all: nothing was asked for, and the root is not inspected.

#### Contact the implementer

| Condition | What the failure says |
| --- | --- |
| The entry assembly references a newer version of a contract assembly than the host carries | Names the plugin, the contract, the version required, and the version the host carries. Read before any type is loaded, so it is never a raw type-load error. |
| The plugin's `.deps.json` declares a higher assembly version than the host carries for an assembly the host also has | Names the plugin, the assembly, the version declared, and the version the host carries. |
| A managed assembly the plugin requests at run time resolves to a host copy older than its reference declares | Names the same four things. This is the backstop for a reference the `.deps.json` check could not see; see the known gaps below. |
| The plugin directory has no `.deps.json` | Names the plugin. Its private dependencies would not resolve, failing on a request rather than at startup. |
| The `.deps.json` declares a `runtimepack` library, which a self-contained publish produces | Names the plugin. A plugin is published framework-dependent. |
| The entry assembly exposes zero, or more than one, plugin class | Names what was found. |
| The plugin's declared name does not match its directory name | Names both. Compared ordinally. |
| The entry assembly's own assembly name does not match the directory name | Names both. |
| A contribution hook throws | Names the plugin and the composition phase. |
| The plugin registers a service type the host owns, or removes, replaces, or overwrites a host-owned registration that existed before its hook ran | Names the plugin and the service type, before the call reaches the host's own collection. Plugins contribute registrations; they do not edit the host's. Removing a registration the plugin itself added is fine, and removing a pre-existing framework or third-party registration is permitted and recorded in the inventory. |
| The plugin removes, replaces, or overwrites one of the host's four logging registrations | Names the plugin and the service type. `ClearProviders()` lands here: it would silence DMS and the inventory record of the plugin doing it. Adding a logging **provider** is untouched. |
| The plugin **adds** its own unkeyed logger factory or logger registration | Names the plugin and the service type. Those are resolved singly, so the last registration wins and a plugin would displace the host's logging by adding rather than removing. Keyed logging registrations and added providers stay permitted. |
| The plugin registers a declared contract under a wildcard service key | Names the contract. Enumerable resolution cannot reach a wildcard registration, so the startup probe cannot cover it. |
| The plugin leaves no surviving declared contract registration | Names the plugin and lists what it did register. It ran and contributed nothing the host will call — most often a plugin allowlisted on the wrong host, or a replace-contract claim made with a `TryAdd`, which declines invisibly. |
| A registration of a declared contract cannot be constructed | Reported by the startup probe, which resolves each surviving declared-contract registration once before the process serves traffic. |

Every row in this group is about the plugin's own content rather than about how the
deployment is assembled, which is why its author is the party who can change it. Some
of them have a second remedy: the two version-skew rows and the runtime backstop name
a version **the host** carries, so running a host version the plugin already supports
resolves them as surely as a new plugin build would. Either way the allowlist is the
operator's immediate lever, and removing the plugin's name is what gets DMS started
again.

#### Resolve a conflict between two plugins

| Condition | What the failure says |
| --- | --- |
| Two plugins register the same single-claimant contract | Names both plugins and the contract. Only an operator can resolve it, by removing one of the two names from `Plugins:Allowed`. |
| One plugin registers the same single-claimant contract twice | Names that plugin and its registration count. No allowlist change can choose between two registrations made inside one plugin, so this one goes to its author. |

#### Known gaps, stated rather than implied

- **An unmanaged library a plugin declares but cannot resolve is not detected at
  load.** Native resolution is lazy by construction, so it surfaces as a
  `DllNotFoundException` on first use.
- **A managed assembly whose host copy is older than the plugin's reference declares
  refuses on the request thread when that first use falls after startup.** The
  refusal itself is the backstop row above; what changes is how it reaches you. The
  readable message is produced by a frame inside the loader, and once startup has
  completed there is no such frame: the refusal travels up the request pipeline as
  repeated HTTP 500s, with no process abort and no unwrapped message.

#### Two rules that apply only to configuration contribution

The plugin contract exposes a service-contribution hook and no configuration-contribution
hook, so neither rule below can be reached by any plugin a deployment can install. They
are recorded so the catalogue states the whole rule set rather than a silently partial
one:

- **A configuration hook that removes or reorders a source it did not add is
  fatal**, naming the plugin. Adding is the only permitted operation.
- **The "contributed nothing" row above has a second half that is likewise
  unreachable.** A plugin satisfies that row by registering a declared contract;
  where configuration contribution is supported, contributing a configuration source
  satisfies it as well.

### Where to read the reason

The two questions have two answers, and they live in different places.

**Why startup failed: the startup status file.** Its path is
`AppSettings:StartupStatusFilePath`, defaulting to `dms-startup-status.json` in the
system temporary directory. It is a JSON document carrying the status, the bootstrap
phase, a one-line summary, and — on a failure — the exception type and message. A
plugin failure names its phase there: the loading phase for a failure to load, and
the service-configuration phase for a failure inside a contribution hook, because the
host invokes that hook while it is registering services.

**What actually loaded: the log.** Each loaded plugin produces one
`Plugin inventory for {PluginName} version {AssemblyVersion}` event listing the files
the plugin declared, the service types it registered, the registrations it removed,
and the host-first substitutions recorded up to that point. Because the design cannot
contain a plugin, that event is an **audit record**: it is how an incident responder
says which third-party code was available to the process, at what version, from which
bytes.

**What the substitution list does and does not prove.** It is narrower than the other
three, and an investigation into a dependency mismatch has to read it as what it is: a
startup snapshot of the version-changing substitutions observed so far. The event is
emitted once, before any startup task runs, and a resolution is recorded only where
the version the host served differs from the version the plugin declared. A
same-version substitution leaves no row, and neither does a resolution first triggered
after the event was written. An assembly's absence from the list is therefore not
evidence that the plugin's own copy ran.

**One ordering caveat.** Plugin loading runs before the logging pipeline exists, so
the loader writes its own lines to standard error. Its warnings are replayed through
the application logger once one exists, as `Plugin loader warning` events, so a
deployment that collects application logs rather than container output still sees
them. A **fatal** during loading is not replayed, because the process does not reach
the point where a logger exists: for that, read standard error and the startup status
file.

### The Compose blocks above are asserted equal to the committed files

`eng/docker-compose/plugins-dms.yml` and `eng/docker-compose/plugins-fetch-dms.yml`
are the artifact. The end-to-end tiers run those files as committed, and a check in
this repository compares each block above against its file, so a chapter edited
without the file — or a file edited without the chapter — fails. Nothing anywhere
drives a recipe parsed out of this document, which would prove the document rather
than the file an operator actually composes with `-f`.

For running the recipes against a local development stack through
`bootstrap-local-dms.ps1`, see
[eng/docker-compose/README.md](../eng/docker-compose/README.md).

## Logging

See [Logging Policy](./LOGGING.md)

## Observability

Observability is closely related to logging, but goes beyond it. Open Telemetry
is an emerging standard for observability.

The following article provides additional information about Open Telemetry and
how it might be useful in the DMS platform. The article references the Project
Meadowlark application stack, but is equally applicable here: [What Is Open
Telemetry?](https://github.com/Ed-Fi-Exchange-OSS/Meadowlark/blob/main/docs/design/open-telemetry/README.md)

For the metrics the Ed-Fi API publishes for collection reads — traditional
paging, cursor paging, and partition planning — including the instrument names,
units, dimensions, and what collecting them requires of the host, see
[Collection Paging Telemetry](./PAGING-TELEMETRY.md).

## Security

### Transport Encryption

Those who are hosting the application are strongly encouraged to use TLS binding
at least at the gateway level. When running a container network, mutual TLS will
provide greater security in case someone is able to elevate privileges on one of
the services.

### Rate Limiting

The application gateway remains the best place to apply rate limiting and DoS
controls. Only the Ed-Fi API has a built-in rate limiter as a fallback; the
Configuration Service has none today. The API's limiter partitions on the raw
Host header value: clients sharing a hostname share one bucket, while varied
Host values create separate buckets, so it neither isolates clients nor
strictly bounds total backend load. It is a coarse backstop only and must not
be relied on for DoS or brute-force protection.

### Authentication and Authorization

See [Roles and Scopes](./ROLES-SCOPES.md)
