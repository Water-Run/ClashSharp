# CycloneDX 1.6 validation schemas

These unmodified files are vendored from the CycloneDX specification's `1.6` tag:

- [bom-1.6.schema.json](https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/bom-1.6.schema.json)
- [spdx.schema.json](https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/spdx.schema.json)
- [jsf-0.82.schema.json](https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/jsf-0.82.schema.json)

Their original Apache-2.0 license is included in `LICENSE`. The source test pins each
schema's SHA-256, bundles the two references in memory, and validates without network
access. The upstream files themselves retain their original contents.

The resulting `bom.cdx.json` describes conservative build inputs, including NuGet
archives, the pinned mihomo executable and four GeoData files. It does not assert
that every package file is deployed, infer dependency edges, or certify complete
GeoData upstream provenance. NuGet's signature-aware content digest is a separate
property; the standard component hash is the actual archive's SHA-256.
