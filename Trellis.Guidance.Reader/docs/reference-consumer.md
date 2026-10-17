# AgentDocs reference consumer

This describes how the reference implementation, `Trellis.Guidance.Reader` and the `agentdocs`
tool, consumes packages that follow the [package format](guidance-v1.md). It is **not** part of
the format: another consumer may behave differently, provided it obeys the format. Commands and
operating procedures are in the [AgentDocs README](../../Trellis.AgentDocs/README.md).

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

## Cross-package reference resolution

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
percent-encoded. Delimiters (`<...>`), titles and the raw fragment suffix (including an empty
`#`, Markdown escapes and entities) retain their original spelling.

Reference resolution is a pure function of the source manifest and verified source bytes,
selected graph metadata, approval policy and verified **target package bytes**. Heading checks
never read an already transformed installed target. Cycles therefore converge regardless of
package iteration order. State records the original source hash, installed canonical hash,
transform version and, per used reference, the target ID/path, outcome, and resolved target
version/document hash when available. A target upgrade or approval change recalculates an
unchanged source document and removes obsolete target directories and placeholders in the same
desired-state plan.

## Canonical text

Analysis operates on AgentDocs canonical text: verified UTF-8 package bytes are decoded, an
optional BOM is removed and CRLF/CR is normalized to LF before parsing. Parser source spans
therefore index the canonical .NET string, not original UTF-8 bytes. Installed files use the
consumer's standard encoding; consumers must preserve canonical text rather than the source
BOM or newline spelling. The installed source can differ from package bytes only through this
canonicalization and declared URL-span rewrites; exact package bytes are always hashed first.

## Markdown parser

The version 1 reference implementation pins Markdig 1.4.0 with precise source locations and
GitHub auto-identifiers. An upgrade of the parser must pass the conformance fixtures unchanged,
because it can change which links and headings are recognized.

## Trust

- The reader executes no package code and neither evaluates MSBuild nor restores or builds. (A
  consumer tool that evaluates projects to validate its own inputs is outside this contract.)
- A consumer decides which packages to read at all. `GuidanceReader.Discover` accepts a
  predicate over package IDs; a package the predicate rejects is reported as `NotLoaded` and its
  manifest and documents are never parsed, hashed, or validated.
