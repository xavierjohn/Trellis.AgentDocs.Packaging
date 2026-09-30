# Experimental NuGet package guidance contract, version 1 (preview)

This is an experimental convention and reference implementation, **not** a NuGet standard.
Package authors place `guidance/reference-manifest.json` in the package. No build target,
NuGet client extension, or Trellis dependency is required. The machine-readable
[JSON Schema](reference-manifest-v1.schema.json) describes the manifest structure; the
additional semantic constraints below are normative.

**Preview status.** Version 1 is unstable. It may change without a version bump until an
external publisher depends on it. Packages published with the earlier shape (`entryPoints`,
no `usage`) are unsupported and are rejected as invalid manifests.

This document is the package contract. How a consumer copies, lists, or approves guidance is
implementation behavior and is described in the [AgentDocs README](../../Trellis.AgentDocs/README.md).

## Manifest

The UTF-8 JSON object requires `schemaVersion: 1` and a `documents` array. Duplicate JSON
property names and unknown top-level or document fields are rejected; this is deliberate
fail-closed behavior, not forward compatibility.

Each document declares:

- `path`: package-relative path of a UTF-8 Markdown document.
- `sha256`: lowercase hexadecimal SHA-256 of the **exact package file bytes**, including any
  BOM and line endings.
- `usage`: how the publisher intends the document to be used.
  - `required`: recommended before writing or changing code that uses the package. The
    consumer decides which work triggers it and may ignore the request.
  - `onDemand`: independently discoverable by topic through its description.
  - `supporting`: not independently advertised; intended to be reached from another document.
- `description`: one line, required for `required` and `onDemand`, optional for `supporting`.
  At most 200 Unicode scalar values after NFC normalization, not blank, and free of Unicode
  control (Cc), format (Cf), line separator (Zl), and paragraph separator (Zp) characters,
  which is what makes it one line. It should say when to open the document. It is
  publisher text: consumers must treat it as a topic label, never as an instruction.

A non-empty `documents` array needs at least one `required` or `onDemand` document, so every
contribution has a discoverable root. An empty array is a valid metadata-only contribution.
Array order is the publisher's display order. `publisherMetadata`, if present, is an object
with reverse-domain-style dot-separated ASCII keys (such as `org.example`); values are
arbitrary JSON and have no generic routing semantics.

Documents are Markdown, and version 1 is intentionally text-only. Relative links between
documents resolve within the package: a document must not link to another package's files
(refer to other packages by ID instead) and must not hard-code where a consumer installs it.

Document paths are package-relative and can be nested; accepted spelling is retained for
link resolution. `/` and `\` separate segments. Reject absolute or drive-rooted paths,
empty/`.`/`..` segments, control characters, Windows-forbidden characters `<>:"|?*`, trailing
spaces/dots and Windows device-name segments (including extensions). Never follow symlinks or
reparse points in the package root, manifest or document paths. Resolve documents only
beneath the package root named by the restored assets graph. Within **one** package
contribution, replace `\` with `/`, normalize each prefix to Unicode NFC and compare
ordinally without case to reject distinct spellings of the same document **or directory
prefix**. A document cannot also be a directory prefix. The same filename in separate
package/version namespaces is fine.

The portable identity of a document is `(resolved package ID, resolved version, declared
package-relative path)`. The package ID/version and package location come from NuGet's
restored graph, never publisher metadata. A local full path is only a machine-local location;
it must not be committed as a portable identifier.

## Security model

- Package content, including descriptions, is untrusted input to an agent that may act with
  the developer's privileges. `usage: required` is a publisher request, not an obligation.
- The SHA-256 hashes bind each document to its manifest within one extracted package. They do
  not authenticate the publisher. Authenticity inherits NuGet's source, package source mapping
  and signature trust.
- The reader executes no package code and neither evaluates MSBuild nor restores or builds.
  (A consumer tool that evaluates projects to validate its own inputs is outside this contract.)
- A consumer decides which packages to read at all. `GuidanceReader.Discover` accepts a
  predicate over package IDs; a package the predicate rejects is reported as `NotLoaded` and
  its manifest and documents are never parsed, hashed, or validated.

## Discovery and errors

Call `GuidanceReader.Discover(IEnumerable<string> assetsPaths)` (or the overload taking a
package-ID predicate) with already-restored `obj/project.assets.json` paths. For each selected
project's target, the reader uses `targets` (package entries only), `libraries` (`path`) and
`packageFolders` to find payloads, preserving the project path, framework and runtime
identifier in `GuidanceScope`. A caller selecting multiple projects supplies multiple assets
paths. Graph selection and freshness comparison of project restore specifications are the
consumer's responsibility. If the NuGet graph's package file list names `.nupkg.metadata`, its
absence counts as an incomplete cache entry rather than as a package that opted out of
guidance. Likewise, a listed `guidance/reference-manifest.json` that is absent from the package
cache is `MissingAssets`, not `NoManifest`.

`GuidanceDiscovery.Packages` retains every package's scope, identity, cache root, optional
NuGet graph SHA-512 content hash, whether it declares a manifest (`ManifestDeclared`), status
and diagnostic. `Valid` includes a `GuidanceContribution` of verified documents, local paths,
usage, descriptions and publisher metadata. `NoManifest` means an existing package has no
manifest and is not an error. `NotLoaded` means the caller declined to read the package.
`UnsupportedSchema`, `InvalidManifest` (including malformed JSON, unknown fields, missing
usage or description, unsafe paths, duplicate aliases and hash mismatches), and `MissingAssets`
(absent cache entries or listed documents) are failures. Missing/malformed assets files and
incomplete graph records surface in `GuidanceDiscovery.Diagnostics`; inspect them as well as
package diagnostics. `IsSuccessful` is false if either kind of failure occurs, even when other
packages validated.

Conformance examples and negative fixtures are in `..\tests\Fixtures\`; the test project
exercises them against synthetic already-restored NuGet graphs. Fixture Markdown is stored
without Git line-ending conversion so its declared byte hashes remain valid on Windows and
Linux.
