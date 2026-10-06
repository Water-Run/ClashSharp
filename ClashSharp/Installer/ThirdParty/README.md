# Pinned attribution inputs

The catalog binds supplemental license documents to exact NuGet package identities and license
declarations. Documents are original upstream bytes; Git must not normalize their line endings.
The Windows SDK RTF is the document resolved from the license URL declared by the SDK packages.
All other document URLs contain the upstream source commit. No catalog document is executed.

Normal packages are bound to the checked-in packages.lock.json and restored project graph.
SDK download dependencies have exact content hashes here because they are not all represented
as PackageReference entries in those lock files. NuGet signed-package content hashes differ
from ordinary archive hashes: the generator uses the pinned SDK's NuGet reader and verifies
signed-package integrity before comparing the content hash. The output records both kinds.

Update the catalog when a dependency, license declaration, SDK download, or upstream snapshot
changes. Preserve the original bytes and review the source URL, length, and digest together.
New-ClashSharpThirdPartyNotices performs no download and refuses unknown missing licenses.

The generated archive intentionally includes production build dependencies as well as runtime
dependencies. It is an attribution/input inventory, not a claim that every package asset is
distributed. Test-only project dependencies are not included.

GeoData evidence currently covers the pinned generator README, license, workflow, and released
data hashes. Its moving upstream input revisions have not been reconstructed. The inventory
states this explicitly; the generator license does not replace individual upstream terms.
The build additionally emits MIHOMO-SOURCE-MATERIALS.zip from the fixed source-materials.json
contract. It preserves the exact mihomo source commit, vendor and toolchain archives, observed
public CA input, original GPL text and offline compilation script. The package is independently
verified and bound to the bundled executable in payload-provenance.json. This is a pinned
compilation-input archive; it does not claim byte-identical reproduction. GeoData input
reconstruction and final release/platform acceptance remain part of release preparation.
