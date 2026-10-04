# Experimental NuGet package guidance contract, version 1 (preview)

This is an experimental convention and reference implementation, **not** a NuGet standard.
Package authors place `guidance/reference-manifest.json` in the package. No build target,
NuGet client extension, or Trellis dependency is required. The machine-readable
[JSON Schema](reference-manifest-v1.schema.json) describes the manifest structure; the
additional semantic constraints below are normative.

**Preview status.** Version 1 is the first actual alpha contract. No external consumer depends
on an earlier shape, so the contract may change in place while it remains alpha. There is no
backward-compatibility or migration commitment to unpublished preview behavior. Readers reject
every other schema version.

This document is the package contract. How a consumer copies, lists, or approves guidance is
implementation behavior and is described in the [AgentDocs README](../../Trellis.AgentDocs/README.md).

## Manifest

The UTF-8 JSON object requires `schemaVersion: 1` and a `documents` array. Duplicate JSON
property names and unknown top-level or document fields are rejected; this is deliberate
fail-closed behavior, not forward compatibility.

Each document declares:

- `path`: package-relative path of a UTF-8 Markdown document, using `/` separators only
  and without a `.github` segment.
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

`documentReferences`, if present, is an array of cross-package Markdown link declarations.
Each declaration has:

- `path`: a virtual package-relative `.md` path. A source document links to this path with an
  ordinary relative Markdown link. No file at this path is packed.
- `packageId`: the target NuGet package ID (1-100 ASCII letters, digits, dots, hyphens or
  underscores, beginning with a letter or digit).
- `documentPath`: the exact package-relative `.md` path declared in the target package's
  `documents` array.

Reference `path` values obey the document path portability and alias rules and share one
namespace with local documents and other references. A path cannot be both a local document
and a reference or be used as another path's directory prefix. References require at least
one local document. The same target may have more than one virtual path, although authors
should normally declare one. `packageId` must not equal the publishing package's own NuGet ID,
case-insensitively. The MSBuild helper obtains that identity from `$(PackageId)`, package
validation obtains it from the root nuspec, and consumers obtain it from the restored NuGet
graph. All manifest paths use forward slashes only and must not contain a `.github`
segment; this is the state-safe subset used by consumers. Unknown fields on a reference
are rejected.

For example, a document packed at `guide/start.md` can link to
`[YARP](yarp.md#forwarding-actors)` while its manifest declares `guide/yarp.md` as a reference
to package `Trellis.Yarp`, document `guides/yarp.md`. Authors use ordinary Markdown link
destinations, not a custom URI scheme and not a consumer installation path. Cross-package
references in raw HTML `href` or `src` attributes are contract errors because consumers cannot
rewrite them without reserializing publisher text. A virtual link with a query string is also
an error. Angle-bracket destinations, optional link titles and fragments are supported.
Links in code spans and fenced code blocks are examples, not references.

Publisher-side validation and consumer rewriting use the same CommonMark parser and link
analysis. The version 1 reference implementation pins Markdig 1.4.0 with precise source
locations and GitHub auto-identifiers. Validation verifies declaration syntax and whether a
Markdown link uses each virtual path, but it cannot prove that another package or heading will
exist in a future consumer graph. Heading IDs include GitHub-style duplicate suffixes; explicit
HTML anchors do not satisfy a fragment. An unused declaration is a publisher warning and has no
consumer-side effect.

A consumer resolves each **used** reference against the complete selected restored graph, not
one project/TFM scope. One canonical installed file cannot carry scope-dependent bytes, and a
source and target may legitimately be restored by different selected projects. A
repository-level consumer rejects mixed approved versions before resolving references.
Loaded contributions for the same package ID/version must agree on the ordered document
declarations and exact hashes, reference declarations and publisher metadata; disagreement
fails before writing instead of choosing one scope's payload. It
must check approval before touching the target package: an unapproved target contributes only
graph metadata (ID and version), and its package directory, manifest and documents are never
opened.

Resolution has these deterministic outcomes:

| Condition | Outcome |
|---|---|
| Target ID is not in the selected graph | Generated placeholder with reason `not-restored`, plus a warning. |
| Target is restored but unapproved | Generated placeholder with reason `not-approved`, plus a warning. |
| Approved target has no guidance manifest | Generated placeholder with reason `no-guidance`, plus a warning. |
| Approved target manifest does not declare the exact `documentPath` | Generated placeholder with reason `document-missing`, plus a warning. |
| Exact target document exists | Rewrite the parsed Markdown URL span to its canonical installed path. |
| A linked heading is absent | Keep the rewritten document link and original fragment, plus a warning. |
| Approved target has an invalid manifest/hash, or approved versions are mixed | Fail before writing. |
| Source uses raw HTML, a query string, an aliased spelling, or a self-reference | Fail before writing. |

Consumers may offer a strict-reference mode that promotes the warnings above to a failure
before writing. A generated placeholder is a fixed, tool-authored LF template containing only
the validated target ID/path, one reason code and a statement that source guidance remains
available without the page. It contains no version, fragment, timestamp or imperative to
install or approve anything. Placeholders are tool-owned artifacts: they are excluded from
the guidance index, publisher hashes, reachability and `usage` semantics.

Once both source and target bytes pass their manifest hashes, a consumer may replace only the
parsed Markdown URL span with a relative link to the target's canonical installed path.
Relative-path computation uses `/` independently of the host OS; each target segment is
percent-encoded. Delimiters (`<...>`), titles and the raw fragment suffix (including an empty `#`,
Markdown escapes and entities) retain their original spelling. Heading validation uses
the decoded fragment, not its source spelling. Link resolution unescapes percent encoding before matching the
exact declared virtual path. Case-only or Unicode-normalization aliases are errors.

Analysis operates on AgentDocs canonical text: verified UTF-8 package bytes are decoded, an
optional BOM is removed and CRLF/CR is normalized to LF before parsing. Parser source spans
therefore index the canonical .NET string, not original UTF-8 bytes. Installed files use the
consumer's standard encoding; consumers must preserve canonical text rather than the source
BOM or newline spelling. The installed source can differ from package bytes only through this
canonicalization and declared URL-span rewrites; exact package bytes are always hashed first.

Reference resolution is a pure function of the source manifest and verified source bytes,
selected graph metadata, approval policy and verified **target package bytes**. Heading checks
never read an already transformed installed target. Cycles therefore converge regardless of
package iteration order. State records the original source hash, installed canonical hash,
transform version and, per used reference, the target ID/path, outcome, and resolved target
version/document hash when available. A target upgrade or approval change recalculates an
unchanged source document and removes obsolete target directories and placeholders in the same
desired-state plan.

Documents are Markdown, and version 1 is intentionally text-only. Ordinary relative links
resolve to local declared documents or declared `documentReferences`; other package files are
not installed. Documents must never hard-code where a consumer installs guidance.

Virtual links can be dead in a publisher repository because no virtual file is packed.
Monorepos should choose virtual paths that also resolve to the sibling source document when
practical. Other repositories should configure their authoring link checker to allow declared
virtual paths or keep an explicitly repository-only stub out of the package; they must not pack
a duplicate target document.

Document paths are package-relative and can be nested; accepted spelling is retained for
link resolution. Only `/` separates segments; backslashes and `.github` segments are rejected.
Reject absolute or drive-rooted paths,
empty/`.`/`..` segments, control characters, Windows-forbidden characters `<>:"|?*`, trailing
spaces/dots and Windows device-name segments (including extensions). Never follow symlinks or
reparse points in the package root, manifest or document paths. Resolve documents only
beneath the package root named by the restored assets graph. Within **one** package
contribution, normalize each document/reference prefix to Unicode NFC
and compare ordinally without case to reject distinct spellings of the same document,
reference or **directory prefix**. A document or reference cannot also be a directory prefix.
The same filename in separate package/version namespaces is fine.

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
- A cross-package reference is not transitive approval. Resolving one never weakens the
  target package's source trust or explicit package-ID approval requirements.

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
usage, descriptions, portable document references and publisher metadata. Discovery returns
reference identities but does not resolve them; a graph-aware consumer performs the
approval/scope checks described above. `NoManifest` means an existing package has no manifest
and is not an error. `NotLoaded` means the caller declined to read the package.
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
