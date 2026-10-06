# mihomo source materials for ClashSharp 1.0.0

SOURCE-MATERIALS.json binds this archive to the bundled Windows/amd64/v1 mihomo
executable and exact upstream source, vendor, patched Go toolchain and public CA
input. LICENSE preserves the upstream GPL text; additional dependency licenses
remain inside the upstream source and vendor archives.

The source commit's component/ca/ca-certificates.crt is empty. The upstream release
workflow replaces that file before compilation. The supplied CA file contains the
public certificate bytes observed in the exact bundled executable; it contains no
private key and is not imported into any operating-system certificate store.

Extract this ZIP into a new ordinary directory on Linux x86_64 with Python 3.12
or later, then run:

    python3 BUILD-OFFLINE.py

The script first verifies every input digest from SOURCE-MATERIALS.json, extracts
the fixed source/vendor/toolchain with Python's data extraction filter, supplies
the observed CA file, and builds Windows/amd64/v1 with CGO disabled and the
with_gvisor tag. It disables Go downloads and keeps caches and output within a
new build directory. It limits compilation to two workers and fifteen minutes.
It does not run the Windows executable. An existing build directory is preserved;
inspect its result rather than replacing it.

This is a pinned compilation-input archive. The upstream executable records a
modified worktree, and verification deliberately uses a different build timestamp
and omits VCS metadata. Successful compilation is not a byte-identical reproduction
claim. GeoData input traceability and final release/platform acceptance remain
separate checks. Original inputs are preserved without executing catalog data.
