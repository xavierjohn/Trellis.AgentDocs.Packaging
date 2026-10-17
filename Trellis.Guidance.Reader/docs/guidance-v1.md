# AgentDocs NuGet package guidance contract, version 1

This is a convention and reference implementation, **not** a NuGet standard.
Package authors place `guidance/reference-manifest.json` in the package. No build target,
NuGet client extension, or Trellis dependency is required. The machine-readable
[JSON Schema](reference-manifest-v1.schema.json) describes the manifest structure; the
additional semantic constraints below are normative.

This document specifies the package format only: what a publisher puts in a package and what
those files mean. How a consumer discovers, approves, installs, indexes, or rewrites guidance is
consumer behavior and is not part of the format; the reference implementation's behavior is described
in the [reference consumer](reference-consumer.md).

**Stability.** Version 1 is stable. The manifest shape and every normative rule in this
document are fixed for `schemaVersion: 1`. Readers reject unknown fields and every other schema
version (fail closed), so there is no forward compatibility within a version: adding a field,
adding a rule, or tightening or loosening what a reader accepts is a breaking change and
requires `schemaVersion: 2`. `publisherMetadata` is the only extension point. A fix that makes an
implementation match this document is not a contract change. The reference implementation's
conformance fixtures are part of the contract; its Markdown parser is pinned (see the
[reference consumer](reference-consumer.md)), and an upgrade of it must pass them unchanged.

This document is the package contract. How a consumer copies, lists, or approves guidance is
implementation behavior and is described in the [AgentDocs README](../../Trellis.AgentDocs/README.md)
and the [reference consumer](reference-consumer.md).

## Manifest

The UTF-8 JSON object requires `schemaVersion: 1`, written as the integer literal `1` (`1.0`,
`1e0` and `"1"` are invalid), and a `documents` array. Duplicate JSON
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

## Links and references

For example, a document packed at `guide/start.md` can link to
`[YARP](yarp.md#forwarding-actors)` while its manifest declares `guide/yarp.md` as a reference
to package `Trellis.Yarp`, document `guides/yarp.md`. Authors use ordinary Markdown link
destinations, not a custom URI scheme and not a consumer installation path. Cross-package
references in raw HTML `href` or `src` attributes are contract errors because consumers cannot
rewrite them without reserializing publisher text. A virtual link with a query string is also
an error. Angle-bracket destinations, optional link titles and fragments are supported.
Links in code spans and fenced code blocks are examples, not references.

Publisher-side validation and consumer rewriting use the same CommonMark parser and link
analysis. Validation verifies declaration syntax and whether a Markdown link uses each virtual
path, but it cannot prove that another package or heading will exist in a future consumer graph.
Heading IDs include GitHub-style duplicate suffixes; explicit HTML anchors do not satisfy a
fragment. An unused declaration is a publisher warning and has no consumer-side effect.

Heading validation uses the decoded fragment, not its source spelling. Link resolution unescapes
percent encoding before matching the exact declared virtual path. Case-only or
Unicode-normalization aliases are errors.

Documents are Markdown, and version 1 is intentionally text-only. Ordinary relative links
resolve to local declared documents or declared `documentReferences`; other package files are
not installed. Documents must never hard-code where a consumer installs guidance.

Virtual links can be dead in a publisher repository because no virtual file is packed.
Monorepos should choose virtual paths that also resolve to the sibling source document when
practical. Other repositories should configure their authoring link checker to allow declared
virtual paths or keep an explicitly repository-only stub out of the package; they must not pack
a duplicate target document.

## Paths and identity

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

## Trust

- Package content, including descriptions, is untrusted input to an agent that may act with the
  developer's privileges. `usage: required` is a publisher request, not an obligation.
- The SHA-256 hashes bind each document to its manifest within one extracted package. They do
  not authenticate the publisher. Authenticity inherits NuGet's source, package source mapping
  and signature trust.
- A consumer decides which packages to read at all.
- A cross-package reference is not transitive approval. Resolving one never weakens the target
  package's source trust or explicit package-ID approval requirements.

## Conformance

Conformance examples and negative fixtures are in `..\tests\Fixtures\`; the test project
exercises them against synthetic already-restored NuGet graphs. Fixture Markdown is stored
without Git line-ending conversion so its declared byte hashes remain valid on Windows and
Linux.

