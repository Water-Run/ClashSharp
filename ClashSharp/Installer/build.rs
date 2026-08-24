use std::collections::{BTreeMap, BTreeSet};
use std::env;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};
use zip::ZipArchive;

const MAX_TRUSTED_FILE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_TRUSTED_TOTAL_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_MSIX_TOTAL_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_GEODATA_ASSET_BYTES: u64 = 256 * 1024 * 1024;
const MAX_GEODATA_MANIFEST_BYTES: u64 = 64 * 1024;
const MAX_APPX_MANIFEST_BYTES: u64 = 1024 * 1024;
const MAX_APPX_BLOCK_MAP_BYTES: u64 = 16 * 1024 * 1024;
const APPX_MANIFEST_PATH: &str = "AppxManifest.xml";
const APPX_MANIFEST_NAMESPACE: &str =
    "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
const APPX_UAP10_NAMESPACE: &str = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
const APPX_BLOCK_MAP_PATH: &str = "AppxBlockMap.xml";
const APPX_BLOCK_MAP_NAMESPACE: &str = "http://schemas.microsoft.com/appx/2010/blockmap";
const APPX_BLOCK_MAP_SHA256_METHOD: &str = "http://www.w3.org/2001/04/xmlenc#sha256";
const EXPECTED_PACKAGE_ARCHITECTURE: &str = "x64";
const EXPECTED_APPLICATION_ID: &str = "App";
const EXPECTED_APPLICATION_EXECUTABLE: &str = "ClashSharp.exe";
const EXPECTED_APPLICATION_ENTRY_POINT: &str = "Windows.FullTrustApplication";
const SOURCE_APPLICATION_EXECUTABLE: &str = "$targetnametoken$.exe";
const SOURCE_APPLICATION_ENTRY_POINT: &str = "$targetentrypoint$";
const GEODATA_MANIFEST_PATH: &str = "Binaries/GeoData/manifest.json";
const PAYLOAD_PROVENANCE_PATH: &str = "payload-provenance.json";
const EXPECTED_DEPENDENCY_NAME: &str = "Microsoft.WindowsAppRuntime.1.8";
const EXPECTED_DEPENDENCY_PUBLISHER: &str =
    "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
const REQUIRED_GEODATA_ASSETS: [(&str, &str); 4] = [
    ("Country.mmdb", "binaries/geodata/country.mmdb"),
    ("GeoIP.dat", "binaries/geodata/geoip.dat"),
    ("GeoSite.dat", "binaries/geodata/geosite.dat"),
    ("ASN.mmdb", "binaries/geodata/asn.mmdb"),
];
const ALLOWED_GEODATA_PATHS: [&str; 5] = [
    GEODATA_MANIFEST_PATH,
    "Binaries/GeoData/Country.mmdb",
    "Binaries/GeoData/GeoIP.dat",
    "Binaries/GeoData/GeoSite.dat",
    "Binaries/GeoData/ASN.mmdb",
];
const REQUIRED_PACKAGE_FILES: [&str; 23] = [
    "[Content_Types].xml",
    "AppxBlockMap.xml",
    "AppxManifest.xml",
    "AppxMetadata/CodeIntegrity.cat",
    "AppxSignature.p7x",
    "resources.pri",
    "ClashSharp.exe",
    "ClashSharp.dll",
    "ClashSharp.deps.json",
    "ClashSharp.runtimeconfig.json",
    "ClashSharp.RecoveryWatchdog.exe",
    "ClashSharp.RecoveryWatchdog.dll",
    "ClashSharp.RecoveryWatchdog.deps.json",
    "ClashSharp.RecoveryWatchdog.runtimeconfig.json",
    "Binaries/mihomo.exe",
    "Binaries/mihomo-LICENSE.txt",
    "Binaries/mihomo-NOTICE.txt",
    "Binaries/mihomo-manifest.json",
    "Binaries/Service/ClashSharp.MihomoService.exe",
    "Binaries/Service/ClashSharp.MihomoService.dll",
    "Binaries/Service/ClashSharp.MihomoService.deps.json",
    "Binaries/Service/ClashSharp.MihomoService.runtimeconfig.json",
    GEODATA_MANIFEST_PATH,
];
const REQUIRED_PACKAGE_EXECUTABLES: [&str; 4] = [
    "ClashSharp.exe",
    "ClashSharp.RecoveryWatchdog.exe",
    "Binaries/mihomo.exe",
    "Binaries/Service/ClashSharp.MihomoService.exe",
];
// Content_Types is container-only. The other three footprint files are installed, but Windows
// deployment can rewrite them, so only the block-map payload is safe to compare byte-for-byte.
const NON_HASH_STABLE_REGISTERED_MSIX_FILES: [&str; 4] = [
    "[Content_Types].xml",
    "AppxBlockMap.xml",
    "AppxMetadata/CodeIntegrity.cat",
    "AppxSignature.p7x",
];
const REQUIRED_PACKAGE_ASSETS: [&str; 22] = [
    "Assets/LockScreenLogo.scale-200.png",
    "Assets/Logo.png",
    "Assets/SplashScreen.scale-200.png",
    "Assets/Square150x150Logo.scale-200.png",
    "Assets/Square44x44Logo.scale-200.png",
    "Assets/Square44x44Logo.targetsize-24_altform-unplated.png",
    "Assets/StoreLogo.png",
    "Assets/Wide310x150Logo.scale-200.png",
    "Assets/Flags/cn.png",
    "Assets/Flags/de.png",
    "Assets/Flags/fr.png",
    "Assets/Flags/gb.png",
    "Assets/Flags/hk.png",
    "Assets/Flags/jp.png",
    "Assets/Flags/kr.png",
    "Assets/Flags/mo.png",
    "Assets/Flags/sg.png",
    "Assets/Flags/tw.png",
    "Assets/Flags/un.png",
    "Assets/Flags/us.png",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.Core.winmd",
];

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct GeoDataManifest {
    #[serde(rename = "schemaVersion")]
    schema_version: u64,
    files: Vec<GeoDataManifestEntry>,
}

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct GeoDataManifestEntry {
    name: String,
    length: u64,
    sha256: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct AppxPackageIdentity {
    name: String,
    publisher: String,
    publisher_id: String,
    family_name: String,
    version: String,
    architecture: String,
    application_id: String,
    application_executable: String,
    application_entry_point: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ManifestIdentity {
    name: String,
    publisher: String,
    version: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct AppxManifestContract {
    identity: AppxPackageIdentity,
    dependencies: Vec<AppxPackageDependency>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct AppxPackageDependency {
    name: String,
    publisher: String,
    min_version: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct DependencyPackageIdentity {
    name: String,
    publisher: String,
    version: String,
    architecture: String,
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadProvenance {
    schema_version: u64,
    primary: PrimaryPackageProvenance,
    certificate: CertificateProvenance,
    dependencies: Vec<DependencyPackageProvenance>,
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PrimaryPackageProvenance {
    path: String,
    length: u64,
    sha256: String,
    name: String,
    publisher: String,
    version: String,
    architecture: String,
    signer_subject: String,
    signer_thumbprint: String,
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CertificateProvenance {
    path: String,
    length: u64,
    sha256: String,
    subject: String,
    thumbprint: String,
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DependencyPackageProvenance {
    path: String,
    length: u64,
    sha256: String,
    name: String,
    publisher: String,
    version: String,
    architecture: String,
    signer_subject: String,
    signer_thumbprint: String,
    signature_timestamp: bool,
}

#[cfg(not(test))]
fn main() {
    println!("cargo:rerun-if-changed=payload");
    println!("cargo:rerun-if-env-changed=CLASHSHARP_INSTALLER_PAYLOAD_DIR");
    println!("cargo:rerun-if-changed=ui/main.slint");
    println!("cargo:rerun-if-changed=LogoInstaller.ico");
    println!("cargo:rerun-if-changed=../ClashSharp/Package.appxmanifest");
    println!("cargo:rerun-if-env-changed=CLASHSHARP_INSTALLER_PACKAGING_MODE");

    let profile = env::var("PROFILE").unwrap_or_default();
    if profile == "release"
        && !matches!(
            env::var("CLASHSHARP_INSTALLER_PACKAGING_MODE").as_deref(),
            Ok("official" | "development")
        )
    {
        panic!(
            "release Installer builds must run through build.ps1 so signing and input gates cannot be skipped"
        );
    }

    slint_build::compile("ui/main.slint").unwrap();

    let output = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR is required"))
        .join("payload_trust_anchor.rs");
    let payload_directory = env::var_os("CLASHSHARP_INSTALLER_PAYLOAD_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("payload"));
    match generate_payload_trust_anchor(&payload_directory) {
        Ok(source) => fs::write(&output, source).expect("write payload trust anchor"),
        Err(error) if profile == "release" => {
            panic!("release Installer trust anchor generation failed: {error}")
        }
        Err(error) => {
            println!("cargo:warning=Installer machine trust anchor unavailable: {error}");
            let source = unavailable_anchor(Path::new("../ClashSharp/Package.appxmanifest"))
                .unwrap_or_else(|fallback_error| {
                    panic!(
                        "Installer source identity fallback generation failed after {error}: {fallback_error}"
                    )
                });
            fs::write(&output, source).expect("write unavailable trust anchor");
        }
    }

    #[cfg(windows)]
    {
        let mut resource = winresource::WindowsResource::new();
        resource.set_icon("LogoInstaller.ico");
        resource.compile().unwrap();
    }
}

fn generate_payload_trust_anchor(payload: &Path) -> Result<String, String> {
    ensure_payload_root_is_ordinary(payload)?;
    let entries = fs::read_dir(payload)
        .map_err(|error| format!("payload directory unavailable: {error}"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| format!("payload directory read failed: {error}"))?;
    let packages = entries
        .iter()
        .map(|entry| entry.path())
        .filter(|path| {
            path.extension()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case("msix"))
        })
        .collect::<Vec<_>>();
    if packages.len() != 1 {
        return Err(format!(
            "expected exactly one top-level MSIX, found {}",
            packages.len()
        ));
    }
    let certificate = payload.join("ClashSharp_TemporaryKey.cer");
    if !certificate.is_file() {
        return Err(String::from("packaged signing CER is missing"));
    }

    let msix_hash = hash_file(&packages[0])?;
    let certificate_hash = hash_file(&certificate)?;
    let payload_files = enumerate_payload_files(payload)?;
    let provenance = read_payload_provenance(payload)?;
    let primary_relative = relative_payload_path(payload, &packages[0])?;
    let certificate_relative = relative_payload_path(payload, &certificate)?;
    if !payload_files.contains_key(&primary_relative)
        || !payload_files.contains_key(&certificate_relative)
    {
        return Err(String::from("primary MSIX or CER escaped payload manifest"));
    }
    let package = File::open(&packages[0]).map_err(|error| format!("open MSIX failed: {error}"))?;
    let mut archive =
        ZipArchive::new(package).map_err(|error| format!("open MSIX ZIP failed: {error}"))?;
    let package_contract = extract_trusted_package_manifest(&mut archive)?;
    validate_final_msix_file_contract(&mut archive)?;
    let block_map_bytes =
        read_bounded_zip_entry(&mut archive, APPX_BLOCK_MAP_PATH, MAX_APPX_BLOCK_MAP_BYTES)?;
    let block_map_files = parse_appx_block_map_file_manifest(&block_map_bytes)?;
    validate_payload_provenance_and_dependencies(
        payload,
        &payload_files,
        &primary_relative,
        &certificate_relative,
        &package_contract,
        &provenance,
    )?;
    let mut archive_files = BTreeMap::<String, (u64, String)>::new();
    let mut registered_package_files = BTreeMap::<String, (u64, String)>::new();
    let mut trusted_files = BTreeMap::<String, (u64, String)>::new();
    let mut geodata_manifest_bytes = None;
    let mut trusted_total_bytes = 0_u64;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| format!("read MSIX entry failed: {error}"))?;
        let name = entry.name().to_owned();
        let normalized_name = name.to_ascii_lowercase();
        let is_geodata = normalized_name.starts_with("binaries/geodata/");
        if is_geodata && !ALLOWED_GEODATA_PATHS.contains(&name.as_str()) {
            return Err(format!("unexpected GeoData MSIX entry: {name}"));
        }
        let trusted =
            name == "Binaries/mihomo.exe" || name.starts_with("Binaries/Service/") || is_geodata;
        if entry.is_dir()
            || name.ends_with('/')
            || name.contains('\\')
            || entry.enclosed_name().is_none()
            || entry
                .unix_mode()
                .is_some_and(|mode| mode & 0o170_000 == 0o120_000)
        {
            return Err(format!("unsafe final MSIX entry: {name}"));
        }
        if entry.size() == 0 || entry.size() > MAX_TRUSTED_FILE_BYTES {
            return Err(format!("final MSIX entry has invalid length: {name}"));
        }
        let capture_geodata_manifest = name == GEODATA_MANIFEST_PATH;
        if capture_geodata_manifest && entry.size() > MAX_GEODATA_MANIFEST_BYTES {
            return Err(String::from("GeoData manifest exceeds its size budget"));
        }
        if trusted {
            trusted_total_bytes = trusted_total_bytes
                .checked_add(entry.size())
                .ok_or_else(|| String::from("trusted payload length overflow"))?;
            if trusted_total_bytes > MAX_TRUSTED_TOTAL_BYTES {
                return Err(String::from("trusted machine payload exceeds size budget"));
            }
        }

        let mut hasher = Sha256::new();
        let mut actual_length = 0_u64;
        let mut captured_bytes = if capture_geodata_manifest {
            Vec::with_capacity(entry.size() as usize)
        } else {
            Vec::new()
        };
        let mut buffer = [0_u8; 64 * 1024];
        loop {
            let count = entry
                .read(&mut buffer)
                .map_err(|error| format!("read final MSIX entry failed: {error}"))?;
            if count == 0 {
                break;
            }
            actual_length = actual_length
                .checked_add(count as u64)
                .ok_or_else(|| String::from("final MSIX entry length overflow"))?;
            hasher.update(&buffer[..count]);
            if capture_geodata_manifest {
                captured_bytes.extend_from_slice(&buffer[..count]);
            }
        }
        if actual_length != entry.size() {
            return Err(format!("final MSIX entry length changed: {name}"));
        }
        let hash = lower_hex(&hasher.finalize());
        if archive_files
            .insert(normalized_name.clone(), (actual_length, hash.clone()))
            .is_some()
        {
            return Err(format!("case-colliding final MSIX entry: {name}"));
        }
        if !is_non_hash_stable_registered_msix_file(&name) {
            let previous = registered_package_files
                .insert(normalized_name.clone(), (actual_length, hash.clone()));
            debug_assert!(previous.is_none());
        }
        if trusted
            && trusted_files
                .insert(normalized_name, (actual_length, hash))
                .is_some()
        {
            return Err(format!("case-colliding trusted MSIX entry: {name}"));
        }
        if capture_geodata_manifest && geodata_manifest_bytes.replace(captured_bytes).is_some() {
            return Err(String::from("duplicate GeoData manifest MSIX entry"));
        }
    }

    validate_registered_manifest_matches_block_map(&registered_package_files, &block_map_files)?;

    for required in [
        "binaries/mihomo.exe",
        "binaries/service/clashsharp.mihomoservice.exe",
        "binaries/geodata/manifest.json",
        "binaries/geodata/country.mmdb",
        "binaries/geodata/geoip.dat",
        "binaries/geodata/geosite.dat",
        "binaries/geodata/asn.mmdb",
    ] {
        if !trusted_files.contains_key(required) {
            return Err(format!(
                "required trusted MSIX entry is missing: {required}"
            ));
        }
    }
    validate_geodata_manifest(
        geodata_manifest_bytes
            .as_deref()
            .ok_or_else(|| String::from("GeoData manifest MSIX entry is missing"))?,
        &trusted_files,
    )?;

    let mut source = String::from("pub const TRUST_ANCHOR_AVAILABLE: bool = true;\n");
    writeln!(
        source,
        "pub const TRUSTED_MSIX_SHA256: &str = \"{msix_hash}\";"
    )
    .unwrap();
    write_package_identity_constants(&mut source, &package_contract.identity);
    writeln!(
        source,
        "pub const TRUSTED_CERTIFICATE_SHA256: &str = \"{certificate_hash}\";"
    )
    .unwrap();
    writeln!(
        source,
        "pub const TRUSTED_PRIMARY_MSIX_RELATIVE_PATH: &str = {primary_relative:?};"
    )
    .unwrap();
    writeln!(
        source,
        "pub const TRUSTED_CERTIFICATE_RELATIVE_PATH: &str = {certificate_relative:?};"
    )
    .unwrap();
    source.push_str("pub const TRUSTED_PAYLOAD_FILES: &[(&str, u64, &str)] = &[\n");
    for (path, (length, hash)) in payload_files {
        writeln!(source, "    ({path:?}, {length}, \"{hash}\"),").unwrap();
    }
    source.push_str("];\n");
    source.push_str("pub const TRUSTED_ARCHIVE_FILES: &[(&str, u64, &str)] = &[\n");
    for (path, (length, hash)) in archive_files {
        writeln!(source, "    ({path:?}, {length}, \"{hash}\"),").unwrap();
    }
    source.push_str("];\n");
    source.push_str("pub const TRUSTED_REGISTERED_PACKAGE_FILES: &[(&str, u64, &str)] = &[\n");
    for (path, (length, hash)) in registered_package_files {
        writeln!(source, "    ({path:?}, {length}, \"{hash}\"),").unwrap();
    }
    source.push_str("];\n");
    source.push_str("pub const TRUSTED_MACHINE_FILES: &[(&str, u64, &str)] = &[\n");
    for (path, (length, hash)) in trusted_files {
        writeln!(source, "    ({path:?}, {length}, \"{hash}\"),").unwrap();
    }
    source.push_str("];\n");
    Ok(source)
}

fn extract_trusted_package_manifest(
    archive: &mut ZipArchive<File>,
) -> Result<AppxManifestContract, String> {
    parse_final_appx_manifest(&read_canonical_appx_manifest(archive)?)
}

fn read_canonical_appx_manifest(archive: &mut ZipArchive<File>) -> Result<String, String> {
    let manifest_entries = archive
        .file_names()
        .enumerate()
        .filter(|(_, name)| name.eq_ignore_ascii_case(APPX_MANIFEST_PATH))
        .map(|(index, name)| (index, name.to_owned()))
        .collect::<Vec<_>>();
    if manifest_entries.len() != 1 || manifest_entries[0].1 != APPX_MANIFEST_PATH {
        return Err(String::from(
            "MSIX must contain exactly one canonical AppxManifest.xml",
        ));
    }

    let mut entry = archive
        .by_index(manifest_entries[0].0)
        .map_err(|error| format!("read AppxManifest.xml failed: {error}"))?;
    if entry.is_dir()
        || entry.name().contains('/')
        || entry.name().contains('\\')
        || entry.enclosed_name().is_none()
        || entry.size() == 0
        || entry.size() > MAX_APPX_MANIFEST_BYTES
        || entry
            .unix_mode()
            .is_some_and(|mode| mode & 0o170_000 == 0o120_000)
    {
        return Err(String::from("AppxManifest.xml entry is unsafe"));
    }

    let expected_length = entry.size() as usize;
    let mut bytes = Vec::with_capacity(expected_length);
    entry
        .read_to_end(&mut bytes)
        .map_err(|error| format!("read AppxManifest.xml failed: {error}"))?;
    if bytes.len() != expected_length {
        return Err(String::from("AppxManifest.xml length changed"));
    }
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
    std::str::from_utf8(bytes)
        .map(str::to_owned)
        .map_err(|_| String::from("AppxManifest.xml is not canonical UTF-8"))
}

#[cfg(test)]
fn parse_final_appx_identity(manifest: &str) -> Result<AppxPackageIdentity, String> {
    Ok(parse_final_appx_manifest(manifest)?.identity)
}

fn parse_final_appx_manifest(manifest: &str) -> Result<AppxManifestContract, String> {
    let document = parse_appx_document(manifest)?;
    let package = canonical_package_element(&document)?;
    validate_package_integrity_contract(package)?;
    let manifest_identity = parse_manifest_identity(package)?;
    let identity = one_direct_child(package, "Identity")?;
    let architecture = required_attribute(identity, "ProcessorArchitecture")?;
    if architecture != EXPECTED_PACKAGE_ARCHITECTURE {
        return Err(format!(
            "AppxManifest.xml package architecture must be {EXPECTED_PACKAGE_ARCHITECTURE}"
        ));
    }

    let applications = one_direct_child(package, "Applications")?;
    let application = one_direct_child(applications, "Application")?;
    let application_id = required_attribute(application, "Id")?;
    let application_executable = required_attribute(application, "Executable")?;
    let application_entry_point = required_attribute(application, "EntryPoint")?;
    validate_application_contract(
        &application_id,
        &application_executable,
        &application_entry_point,
        EXPECTED_APPLICATION_EXECUTABLE,
        EXPECTED_APPLICATION_ENTRY_POINT,
    )?;

    let identity = complete_package_identity(
        manifest_identity,
        architecture,
        application_id,
        application_executable,
        application_entry_point,
    )?;
    let dependencies = parse_package_dependencies(package)?;
    Ok(AppxManifestContract {
        identity,
        dependencies,
    })
}

fn parse_source_appx_identity(manifest: &str) -> Result<AppxPackageIdentity, String> {
    let document = parse_appx_document(manifest)?;
    let package = canonical_package_element(&document)?;
    validate_package_integrity_contract(package)?;
    let manifest_identity = parse_manifest_identity(package)?;
    let applications = one_direct_child(package, "Applications")?;
    let application = one_direct_child(applications, "Application")?;
    let application_id = required_attribute(application, "Id")?;
    let application_executable = required_attribute(application, "Executable")?;
    let application_entry_point = required_attribute(application, "EntryPoint")?;
    validate_application_contract(
        &application_id,
        &application_executable,
        &application_entry_point,
        SOURCE_APPLICATION_EXECUTABLE,
        SOURCE_APPLICATION_ENTRY_POINT,
    )?;

    complete_package_identity(
        manifest_identity,
        String::from(EXPECTED_PACKAGE_ARCHITECTURE),
        application_id,
        String::from(EXPECTED_APPLICATION_EXECUTABLE),
        String::from(EXPECTED_APPLICATION_ENTRY_POINT),
    )
}

fn parse_dependency_package_identity(manifest: &str) -> Result<DependencyPackageIdentity, String> {
    let document = parse_appx_document(manifest)?;
    let package = canonical_package_element(&document)?;
    let identity = parse_manifest_identity(package)?;
    let identity_node = one_direct_child(package, "Identity")?;
    let architecture = required_attribute(identity_node, "ProcessorArchitecture")?;
    if architecture != EXPECTED_PACKAGE_ARCHITECTURE {
        return Err(String::from(
            "dependency MSIX architecture is outside the x64 contract",
        ));
    }
    let properties = one_direct_child(package, "Properties")?;
    let framework = one_direct_child(properties, "Framework")?;
    if framework.text().map(str::trim) != Some("true") {
        return Err(String::from("dependency MSIX is not a framework package"));
    }
    Ok(DependencyPackageIdentity {
        name: identity.name,
        publisher: identity.publisher,
        version: identity.version,
        architecture,
    })
}

fn parse_appx_document(manifest: &str) -> Result<roxmltree::Document<'_>, String> {
    if manifest.contains("<!DOCTYPE") || manifest.contains("<!ENTITY") {
        return Err(String::from(
            "AppxManifest.xml contains unsupported declarations",
        ));
    }
    roxmltree::Document::parse(manifest)
        .map_err(|error| format!("AppxManifest.xml XML is invalid: {error}"))
}

fn canonical_package_element<'a, 'input>(
    document: &'a roxmltree::Document<'input>,
) -> Result<roxmltree::Node<'a, 'input>, String> {
    let package = document.root_element();
    if package.tag_name().name() != "Package"
        || package.tag_name().namespace() != Some(APPX_MANIFEST_NAMESPACE)
    {
        return Err(String::from(
            "AppxManifest.xml root Package namespace is invalid",
        ));
    }
    Ok(package)
}

fn one_direct_child<'a, 'input>(
    parent: roxmltree::Node<'a, 'input>,
    name: &str,
) -> Result<roxmltree::Node<'a, 'input>, String> {
    let matches = parent
        .children()
        .filter(|node| {
            node.is_element()
                && node.tag_name().name() == name
                && node.tag_name().namespace() == Some(APPX_MANIFEST_NAMESPACE)
        })
        .collect::<Vec<_>>();
    if matches.len() != 1 {
        return Err(format!(
            "AppxManifest.xml must contain exactly one direct {name} element"
        ));
    }
    Ok(matches[0])
}

fn parse_manifest_identity(package: roxmltree::Node<'_, '_>) -> Result<ManifestIdentity, String> {
    let identity = one_direct_child(package, "Identity")?;
    let name = required_attribute(identity, "Name")?;
    if !(3..=50).contains(&name.len())
        || !name
            .bytes()
            .all(|value| value.is_ascii_alphanumeric() || matches!(value, b'.' | b'-'))
    {
        return Err(String::from(
            "AppxManifest.xml Identity Name is noncanonical",
        ));
    }
    let publisher = required_attribute(identity, "Publisher")?;
    if publisher.encode_utf16().count() > 8192 {
        return Err(String::from(
            "AppxManifest.xml Identity Publisher is too long",
        ));
    }
    let version = required_attribute(identity, "Version")?;
    if !is_canonical_package_version(&version) {
        return Err(String::from(
            "AppxManifest.xml Identity Version is noncanonical",
        ));
    }
    Ok(ManifestIdentity {
        name,
        publisher,
        version,
    })
}

fn parse_package_dependencies(
    package: roxmltree::Node<'_, '_>,
) -> Result<Vec<AppxPackageDependency>, String> {
    let dependencies = one_direct_child(package, "Dependencies")?;
    let package_dependencies = dependencies
        .children()
        .filter(|node| {
            node.is_element()
                && node.tag_name().name() == "PackageDependency"
                && node.tag_name().namespace() == Some(APPX_MANIFEST_NAMESPACE)
        })
        .collect::<Vec<_>>();
    if package_dependencies.len() != 1 {
        return Err(String::from(
            "AppxManifest.xml must declare exactly one PackageDependency",
        ));
    }

    let dependency = package_dependencies[0];
    let name = required_attribute(dependency, "Name")?;
    let publisher = required_attribute(dependency, "Publisher")?;
    let min_version = required_attribute(dependency, "MinVersion")?;
    if name != EXPECTED_DEPENDENCY_NAME
        || publisher != EXPECTED_DEPENDENCY_PUBLISHER
        || !is_canonical_package_version(&min_version)
    {
        return Err(String::from(
            "AppxManifest.xml PackageDependency is outside the exact product contract",
        ));
    }
    Ok(vec![AppxPackageDependency {
        name,
        publisher,
        min_version,
    }])
}

fn validate_package_integrity_contract(package: roxmltree::Node<'_, '_>) -> Result<(), String> {
    let properties = one_direct_child(package, "Properties")?;
    let integrity = properties
        .children()
        .filter(|node| {
            node.is_element()
                && node.tag_name().name() == "PackageIntegrity"
                && node.tag_name().namespace() == Some(APPX_UAP10_NAMESPACE)
        })
        .collect::<Vec<_>>();
    if integrity.len() != 1 {
        return Err(String::from(
            "AppxManifest.xml must enable exactly one uap10:PackageIntegrity",
        ));
    }
    let content = integrity[0]
        .children()
        .filter(|node| {
            node.is_element()
                && node.tag_name().name() == "Content"
                && node.tag_name().namespace() == Some(APPX_UAP10_NAMESPACE)
        })
        .collect::<Vec<_>>();
    if content.len() != 1 || content[0].attribute("Enforcement") != Some("on") {
        return Err(String::from(
            "AppxManifest.xml package content integrity enforcement must be on",
        ));
    }
    Ok(())
}

fn required_attribute(node: roxmltree::Node<'_, '_>, name: &str) -> Result<String, String> {
    let value = node.attribute(name).ok_or_else(|| {
        format!(
            "AppxManifest.xml {} {name} is missing",
            node.tag_name().name()
        )
    })?;
    if value.is_empty() || value.chars().any(char::is_control) {
        return Err(format!(
            "AppxManifest.xml {} {name} is invalid",
            node.tag_name().name()
        ));
    }
    Ok(value.to_owned())
}

fn validate_application_contract(
    application_id: &str,
    executable: &str,
    entry_point: &str,
    expected_executable: &str,
    expected_entry_point: &str,
) -> Result<(), String> {
    if application_id != EXPECTED_APPLICATION_ID {
        return Err(format!(
            "AppxManifest.xml Application Id must be {EXPECTED_APPLICATION_ID}"
        ));
    }
    if executable != expected_executable {
        return Err(format!(
            "AppxManifest.xml Application Executable must be {expected_executable}"
        ));
    }
    if entry_point != expected_entry_point {
        return Err(format!(
            "AppxManifest.xml Application EntryPoint must be {expected_entry_point}"
        ));
    }
    Ok(())
}

fn complete_package_identity(
    manifest: ManifestIdentity,
    architecture: String,
    application_id: String,
    application_executable: String,
    application_entry_point: String,
) -> Result<AppxPackageIdentity, String> {
    let publisher_id = derive_publisher_id(&manifest.publisher)?;
    let family_name = format!("{}_{}", manifest.name, publisher_id);
    verify_package_family_name_with_windows(&manifest.name, &manifest.publisher, &family_name)?;
    Ok(AppxPackageIdentity {
        name: manifest.name,
        publisher: manifest.publisher,
        publisher_id,
        family_name,
        version: manifest.version,
        architecture,
        application_id,
        application_executable,
        application_entry_point,
    })
}

fn derive_publisher_id(publisher: &str) -> Result<String, String> {
    if publisher.is_empty()
        || publisher
            .chars()
            .any(|value| value == '\0' || value.is_control())
    {
        return Err(String::from(
            "AppxManifest.xml Identity Publisher is invalid",
        ));
    }
    let mut encoded = Vec::with_capacity(publisher.encode_utf16().count() * 2);
    for value in publisher.encode_utf16() {
        encoded.extend_from_slice(&value.to_le_bytes());
    }
    let digest = Sha256::digest(&encoded);
    const ALPHABET: &[u8; 32] = b"0123456789abcdefghjkmnpqrstvwxyz";
    let mut publisher_id = String::with_capacity(13);
    for chunk in 0..13 {
        let mut value = 0_u8;
        for offset in 0..5 {
            let bit_index = chunk * 5 + offset;
            let bit = if bit_index < 64 {
                (digest[bit_index / 8] >> (7 - bit_index % 8)) & 1
            } else {
                0
            };
            value = (value << 1) | bit;
        }
        publisher_id.push(ALPHABET[value as usize] as char);
    }
    Ok(publisher_id)
}

#[cfg(windows)]
fn verify_package_family_name_with_windows(
    name: &str,
    publisher: &str,
    expected_family: &str,
) -> Result<(), String> {
    const ERROR_SUCCESS: i32 = 0;
    const ERROR_INSUFFICIENT_BUFFER: i32 = 122;
    const PROCESSOR_ARCHITECTURE_AMD64: u32 = 9;

    #[repr(C)]
    struct PackageId {
        reserved: u32,
        processor_architecture: u32,
        version: u64,
        name: *mut u16,
        publisher: *mut u16,
        resource_id: *mut u16,
        publisher_id: *mut u16,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn PackageFamilyNameFromId(
            package_id: *const PackageId,
            package_family_name_length: *mut u32,
            package_family_name: *mut u16,
        ) -> i32;
    }

    let mut name_wide = name.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
    let mut publisher_wide = publisher.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
    let package_id = PackageId {
        reserved: 0,
        processor_architecture: PROCESSOR_ARCHITECTURE_AMD64,
        version: 0,
        name: name_wide.as_mut_ptr(),
        publisher: publisher_wide.as_mut_ptr(),
        resource_id: std::ptr::null_mut(),
        publisher_id: std::ptr::null_mut(),
    };
    let mut length = 0_u32;
    // SAFETY: package_id points to live NUL-terminated strings, and a null output buffer is
    // the documented size-query form of PackageFamilyNameFromId.
    let first = unsafe {
        PackageFamilyNameFromId(&raw const package_id, &raw mut length, std::ptr::null_mut())
    };
    if first != ERROR_INSUFFICIENT_BUFFER || !(2..=256).contains(&length) {
        return Err(format!(
            "PackageFamilyNameFromId size query failed: {first}"
        ));
    }
    let mut buffer = vec![0_u16; length as usize];
    // SAFETY: buffer has the exact character capacity requested by the first API call and
    // all PACKAGE_ID backing strings remain live and unmoved.
    let second = unsafe {
        PackageFamilyNameFromId(&raw const package_id, &raw mut length, buffer.as_mut_ptr())
    };
    if second != ERROR_SUCCESS || length as usize != buffer.len() || buffer.last() != Some(&0) {
        return Err(format!("PackageFamilyNameFromId failed: {second}"));
    }
    buffer.pop();
    let actual = String::from_utf16(&buffer)
        .map_err(|_| String::from("PackageFamilyNameFromId returned invalid UTF-16"))?;
    if actual != expected_family {
        return Err(format!(
            "derived package family does not match Windows: {actual}"
        ));
    }
    Ok(())
}

#[cfg(not(windows))]
fn verify_package_family_name_with_windows(
    _name: &str,
    _publisher: &str,
    _expected_family: &str,
) -> Result<(), String> {
    Ok(())
}

fn write_package_identity_constants(source: &mut String, identity: &AppxPackageIdentity) {
    for (name, value) in [
        ("TRUSTED_PACKAGE_IDENTITY_NAME", &identity.name),
        ("TRUSTED_PACKAGE_PUBLISHER", &identity.publisher),
        ("TRUSTED_PACKAGE_PUBLISHER_ID", &identity.publisher_id),
        ("TRUSTED_PACKAGE_FAMILY_NAME", &identity.family_name),
        ("TRUSTED_PACKAGE_VERSION", &identity.version),
        ("TRUSTED_PACKAGE_ARCHITECTURE", &identity.architecture),
        ("TRUSTED_APPLICATION_ID", &identity.application_id),
        (
            "TRUSTED_APPLICATION_EXECUTABLE",
            &identity.application_executable,
        ),
        (
            "TRUSTED_APPLICATION_ENTRY_POINT",
            &identity.application_entry_point,
        ),
    ] {
        writeln!(source, "pub const {name}: &str = {value:?};").unwrap();
    }
}

fn validate_final_msix_file_contract(archive: &mut ZipArchive<File>) -> Result<(), String> {
    if archive.is_empty() || archive.len() > 4096 {
        return Err(String::from("final MSIX entry count is outside its budget"));
    }
    let mut actual = BTreeMap::<String, String>::new();
    let mut executables = BTreeSet::<String>::new();
    let mut total_bytes = 0_u64;
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| format!("read final MSIX entry failed: {error}"))?;
        let name = entry.name();
        if entry.is_dir()
            || name.ends_with('/')
            || name.contains('\\')
            || entry.enclosed_name().is_none()
            || entry.size() == 0
            || entry.size() > MAX_TRUSTED_FILE_BYTES
            || entry
                .unix_mode()
                .is_some_and(|mode| mode & 0o170_000 == 0o120_000)
        {
            return Err(format!("unsafe final MSIX entry: {name}"));
        }
        let lower = name.to_ascii_lowercase();
        validate_final_msix_entry_name(name)?;
        if lower.ends_with(".exe") {
            executables.insert(name.to_owned());
        }
        if actual.insert(lower, name.to_owned()).is_some() {
            return Err(format!("case-colliding final MSIX entry: {name}"));
        }
        total_bytes = total_bytes
            .checked_add(entry.size())
            .ok_or_else(|| String::from("final MSIX uncompressed length overflow"))?;
        if total_bytes > MAX_MSIX_TOTAL_BYTES {
            return Err(String::from(
                "final MSIX uncompressed content exceeds its budget",
            ));
        }
    }

    for required in REQUIRED_PACKAGE_FILES
        .into_iter()
        .chain(REQUIRED_PACKAGE_ASSETS)
        .chain(ALLOWED_GEODATA_PATHS)
    {
        if actual
            .get(&required.to_ascii_lowercase())
            .map(String::as_str)
            != Some(required)
        {
            return Err(format!("required final MSIX file is missing: {required}"));
        }
    }
    validate_final_msix_executable_set(&executables)?;

    let mut allowed = REQUIRED_PACKAGE_FILES
        .into_iter()
        .chain(REQUIRED_PACKAGE_ASSETS)
        .chain(ALLOWED_GEODATA_PATHS)
        .map(str::to_owned)
        .collect::<BTreeSet<_>>();
    for (deps_path, prefix) in [
        ("ClashSharp.deps.json", ""),
        ("ClashSharp.RecoveryWatchdog.deps.json", ""),
        (
            "Binaries/Service/ClashSharp.MihomoService.deps.json",
            "Binaries/Service/",
        ),
    ] {
        let bytes = read_bounded_zip_entry(archive, deps_path, 4 * 1024 * 1024)?;
        add_dotnet_dependency_assets(&bytes, prefix, &mut allowed)?;
    }
    for name in actual.values() {
        if !allowed.contains(name) {
            return Err(format!("final MSIX file is outside the allowlist: {name}"));
        }
    }
    Ok(())
}

fn validate_final_msix_entry_name(name: &str) -> Result<(), String> {
    let lower = name.to_ascii_lowercase();
    if lower.ends_with(".pdb")
        || lower.ends_with("/packages.lock.json")
        || lower == "packages.lock.json"
        || ["probe", "sandboxtest", "installer", "updater"]
            .iter()
            .any(|forbidden| lower.contains(forbidden))
    {
        return Err(format!("forbidden final MSIX entry: {name}"));
    }
    Ok(())
}

fn is_non_hash_stable_registered_msix_file(name: &str) -> bool {
    NON_HASH_STABLE_REGISTERED_MSIX_FILES.contains(&name)
}

fn parse_appx_block_map_file_manifest(bytes: &[u8]) -> Result<BTreeMap<String, u64>, String> {
    let text = std::str::from_utf8(bytes)
        .map_err(|_| String::from("final MSIX block map is not UTF-8"))?;
    let document = roxmltree::Document::parse(text)
        .map_err(|_| String::from("final MSIX block map is invalid XML"))?;
    let root = document.root_element();
    if root.tag_name().name() != "BlockMap"
        || root.tag_name().namespace() != Some(APPX_BLOCK_MAP_NAMESPACE)
        || root.attribute("HashMethod") != Some(APPX_BLOCK_MAP_SHA256_METHOD)
    {
        return Err(String::from("final MSIX block map contract is invalid"));
    }

    let mut files = BTreeMap::new();
    for file in root.children().filter(|node| {
        node.is_element()
            && node.tag_name().name() == "File"
            && node.tag_name().namespace() == Some(APPX_BLOCK_MAP_NAMESPACE)
    }) {
        let name = file
            .attribute("Name")
            .ok_or_else(|| String::from("final MSIX block map file name is missing"))?;
        let normalized = normalize_block_map_file_name(name)?;
        let length = file
            .attribute("Size")
            .ok_or_else(|| String::from("final MSIX block map file size is missing"))?
            .parse::<u64>()
            .map_err(|_| String::from("final MSIX block map file size is invalid"))?;
        if length == 0 || length > MAX_TRUSTED_FILE_BYTES {
            return Err(String::from(
                "final MSIX block map file length is outside its budget",
            ));
        }
        if files.insert(normalized, length).is_some() {
            return Err(String::from(
                "final MSIX block map contains a case-colliding file",
            ));
        }
    }
    if files.is_empty() {
        return Err(String::from("final MSIX block map file manifest is empty"));
    }
    Ok(files)
}

fn normalize_block_map_file_name(name: &str) -> Result<String, String> {
    if name.is_empty()
        || name.starts_with(['/', '\\'])
        || name.ends_with(['/', '\\'])
        || name.contains('/')
        || name.contains('%')
    {
        return Err(format!("unsafe final MSIX block map path: {name}"));
    }
    let mut segments = Vec::new();
    for segment in name.split('\\') {
        if segment.is_empty() || matches!(segment, "." | "..") || segment.contains(':') {
            return Err(format!("unsafe final MSIX block map path: {name}"));
        }
        segments.push(segment.to_ascii_lowercase());
    }
    Ok(segments.join("/"))
}

fn validate_registered_manifest_matches_block_map(
    registered_files: &BTreeMap<String, (u64, String)>,
    block_map_files: &BTreeMap<String, u64>,
) -> Result<(), String> {
    if registered_files.len() != block_map_files.len()
        || registered_files
            .iter()
            .any(|(path, (length, _))| block_map_files.get(path).copied() != Some(*length))
    {
        return Err(String::from(
            "final MSIX registered payload does not match AppxBlockMap.xml",
        ));
    }
    Ok(())
}

fn validate_final_msix_executable_set(executables: &BTreeSet<String>) -> Result<(), String> {
    let expected = REQUIRED_PACKAGE_EXECUTABLES
        .into_iter()
        .map(str::to_owned)
        .collect::<BTreeSet<_>>();
    if executables != &expected {
        return Err(format!(
            "final MSIX executable set is invalid: {executables:?}"
        ));
    }
    Ok(())
}

fn read_bounded_zip_entry(
    archive: &mut ZipArchive<File>,
    expected_name: &str,
    maximum_length: u64,
) -> Result<Vec<u8>, String> {
    let matching = archive
        .file_names()
        .enumerate()
        .filter(|(_, name)| name.eq_ignore_ascii_case(expected_name))
        .map(|(index, name)| (index, name.to_owned()))
        .collect::<Vec<_>>();
    if matching.len() != 1 || matching[0].1 != expected_name {
        return Err(format!(
            "final MSIX must contain one canonical {expected_name}"
        ));
    }
    let mut entry = archive
        .by_index(matching[0].0)
        .map_err(|error| format!("read {expected_name} failed: {error}"))?;
    if entry.is_dir()
        || entry.size() == 0
        || entry.size() > maximum_length
        || entry.enclosed_name().is_none()
    {
        return Err(format!("final MSIX {expected_name} is unsafe"));
    }
    let expected_length = entry.size() as usize;
    let mut bytes = Vec::with_capacity(expected_length);
    entry
        .read_to_end(&mut bytes)
        .map_err(|error| format!("read {expected_name} failed: {error}"))?;
    if bytes.len() != expected_length {
        return Err(format!("final MSIX {expected_name} length changed"));
    }
    Ok(bytes)
}

fn add_dotnet_dependency_assets(
    deps_json: &[u8],
    prefix: &str,
    allowed: &mut BTreeSet<String>,
) -> Result<(), String> {
    let document: serde_json::Value = serde_json::from_slice(deps_json)
        .map_err(|error| format!("parse .NET dependency manifest failed: {error}"))?;
    let runtime_target = document
        .get("runtimeTarget")
        .and_then(|value| value.get("name"))
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| String::from(".NET dependency manifest runtimeTarget is missing"))?;
    let target = document
        .get("targets")
        .and_then(|value| value.get(runtime_target))
        .and_then(serde_json::Value::as_object)
        .ok_or_else(|| String::from(".NET dependency manifest target graph is missing"))?;
    let mut asset_count = 0_usize;
    for library in target.values() {
        let library = library
            .as_object()
            .ok_or_else(|| String::from(".NET dependency library entry is invalid"))?;
        for section in ["runtime", "native", "runtimeTargets"] {
            let Some(assets) = library.get(section) else {
                continue;
            };
            let assets = assets
                .as_object()
                .ok_or_else(|| String::from(".NET dependency asset section is invalid"))?;
            for asset in assets.keys() {
                if asset == "_._" {
                    continue;
                }
                add_dotnet_asset_allowlist_paths(asset, prefix, None, allowed)?;
                asset_count += 1;
            }
        }
        let Some(resources) = library.get("resources") else {
            continue;
        };
        let resources = resources
            .as_object()
            .ok_or_else(|| String::from(".NET dependency resources section is invalid"))?;
        for (asset, metadata) in resources {
            let locale = metadata
                .get("locale")
                .and_then(serde_json::Value::as_str)
                .ok_or_else(|| String::from(".NET dependency resource locale is missing"))?;
            add_dotnet_asset_allowlist_paths(asset, prefix, Some(locale), allowed)?;
            asset_count += 1;
        }
    }
    if asset_count == 0 {
        return Err(String::from(".NET dependency manifest asset set is empty"));
    }
    Ok(())
}

fn add_dotnet_asset_allowlist_paths(
    asset: &str,
    prefix: &str,
    locale: Option<&str>,
    allowed: &mut BTreeSet<String>,
) -> Result<(), String> {
    if asset.is_empty()
        || asset.contains('\\')
        || asset.starts_with('/')
        || asset
            .split('/')
            .any(|component| component.is_empty() || matches!(component, "." | ".."))
    {
        return Err(String::from(".NET dependency asset path is invalid"));
    }
    let file_name = asset
        .rsplit('/')
        .next()
        .ok_or_else(|| String::from(".NET dependency asset file name is missing"))?;
    let extension = file_name
        .rsplit_once('.')
        .map(|(_, extension)| extension.to_ascii_lowercase());
    if !matches!(extension.as_deref(), Some("dll" | "winmd")) {
        return Err(String::from(
            ".NET dependency asset is outside the runtime library allowlist",
        ));
    }
    let output_path = match locale {
        Some(locale)
            if !locale.is_empty()
                && locale
                    .bytes()
                    .all(|value| value.is_ascii_alphanumeric() || value == b'-') =>
        {
            format!("{prefix}{locale}/{file_name}")
        }
        Some(_) => return Err(String::from(".NET dependency resource locale is invalid")),
        None => format!("{prefix}{file_name}"),
    };
    allowed.insert(output_path);
    allowed.insert(format!("{prefix}{asset}"));
    Ok(())
}

fn read_payload_provenance(payload: &Path) -> Result<PayloadProvenance, String> {
    let path = payload.join(PAYLOAD_PROVENANCE_PATH);
    let metadata = fs::symlink_metadata(&path)
        .map_err(|error| format!("payload provenance is unavailable: {error}"))?;
    if !metadata.is_file() || metadata_is_reparse_point(&metadata) || metadata.len() > 256 * 1024 {
        return Err(String::from("payload provenance file is unsafe"));
    }
    let bytes =
        fs::read(&path).map_err(|error| format!("read payload provenance failed: {error}"))?;
    serde_json::from_slice(&bytes)
        .map_err(|error| format!("parse payload provenance failed: {error}"))
}

fn validate_payload_provenance_and_dependencies(
    payload: &Path,
    payload_files: &BTreeMap<String, (u64, String)>,
    primary_relative: &str,
    certificate_relative: &str,
    package: &AppxManifestContract,
    provenance: &PayloadProvenance,
) -> Result<(), String> {
    if provenance.schema_version != 1 || package.dependencies.len() != 1 {
        return Err(String::from("payload provenance schema is unsupported"));
    }
    let (primary_length, primary_sha256) = payload_files
        .get(primary_relative)
        .ok_or_else(|| String::from("payload provenance primary package is missing"))?;
    let (certificate_length, certificate_sha256) = payload_files
        .get(certificate_relative)
        .ok_or_else(|| String::from("payload provenance certificate is missing"))?;
    if normalize_provenance_path(&provenance.primary.path)? != primary_relative
        || provenance.primary.length != *primary_length
        || provenance.primary.sha256 != *primary_sha256
        || provenance.primary.name != package.identity.name
        || provenance.primary.publisher != package.identity.publisher
        || provenance.primary.version != package.identity.version
        || provenance.primary.architecture != package.identity.architecture
        || provenance.primary.signer_subject != package.identity.publisher
        || normalize_provenance_path(&provenance.certificate.path)? != certificate_relative
        || provenance.certificate.length != *certificate_length
        || provenance.certificate.sha256 != *certificate_sha256
        || provenance.certificate.subject != package.identity.publisher
        || provenance.certificate.thumbprint != provenance.primary.signer_thumbprint
        || !is_canonical_thumbprint(&provenance.certificate.thumbprint)
    {
        return Err(String::from(
            "payload provenance primary package or certificate does not match",
        ));
    }

    let dependency_paths = payload_files
        .keys()
        .filter(|path| path.starts_with("dependencies/") && path.ends_with(".msix"))
        .collect::<Vec<_>>();
    if dependency_paths.len() != package.dependencies.len()
        || provenance.dependencies.len() != package.dependencies.len()
    {
        return Err(String::from(
            "payload dependency set does not match AppxManifest.xml",
        ));
    }

    let mut seen_provenance_paths = BTreeSet::new();
    for declaration in &package.dependencies {
        let expected_relative = format!(
            "dependencies/x64/{}.msix",
            declaration.name.to_ascii_lowercase()
        );
        let (expected_length, expected_sha256) = payload_files
            .get(&expected_relative)
            .ok_or_else(|| String::from("declared dependency package is missing"))?;
        let matching = provenance
            .dependencies
            .iter()
            .filter(|entry| {
                normalize_provenance_path(&entry.path).as_deref() == Ok(expected_relative.as_str())
            })
            .collect::<Vec<_>>();
        if matching.len() != 1 || !seen_provenance_paths.insert(expected_relative.clone()) {
            return Err(String::from(
                "payload dependency provenance is missing or duplicated",
            ));
        }
        let recorded = matching[0];
        if recorded.length != *expected_length
            || recorded.sha256 != *expected_sha256
            || recorded.name != declaration.name
            || recorded.publisher != declaration.publisher
            || recorded.architecture != EXPECTED_PACKAGE_ARCHITECTURE
            || recorded.signer_subject != declaration.publisher
            || !is_canonical_thumbprint(&recorded.signer_thumbprint)
            || !recorded.signature_timestamp
        {
            return Err(String::from(
                "payload dependency provenance fields are invalid",
            ));
        }

        let dependency_path = payload
            .join("Dependencies")
            .join("x64")
            .join(format!("{}.msix", declaration.name));
        let dependency_file = File::open(&dependency_path)
            .map_err(|error| format!("open dependency MSIX failed: {error}"))?;
        let mut dependency_archive = ZipArchive::new(dependency_file)
            .map_err(|error| format!("open dependency MSIX ZIP failed: {error}"))?;
        let identity = parse_dependency_package_identity(&read_canonical_appx_manifest(
            &mut dependency_archive,
        )?)?;
        if identity.name != declaration.name
            || identity.publisher != declaration.publisher
            || identity.version != recorded.version
            || identity.architecture != EXPECTED_PACKAGE_ARCHITECTURE
            || compare_package_versions(&identity.version, &declaration.min_version)?
                == std::cmp::Ordering::Less
        {
            return Err(String::from(
                "dependency MSIX identity does not match the declared dependency",
            ));
        }
    }
    Ok(())
}

fn normalize_provenance_path(path: &str) -> Result<String, String> {
    if path.is_empty()
        || path.contains('\\')
        || path.starts_with('/')
        || path
            .split('/')
            .any(|component| component.is_empty() || matches!(component, "." | ".."))
    {
        return Err(String::from("payload provenance path is invalid"));
    }
    Ok(path.to_ascii_lowercase())
}

fn is_canonical_thumbprint(value: &str) -> bool {
    value.len() == 40
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'A'..=b'F').contains(&byte))
}

fn compare_package_versions(left: &str, right: &str) -> Result<std::cmp::Ordering, String> {
    Ok(parse_package_version_components(left)?.cmp(&parse_package_version_components(right)?))
}

fn parse_package_version_components(version: &str) -> Result<[u16; 4], String> {
    if !is_canonical_package_version(version) {
        return Err(String::from("package version is noncanonical"));
    }
    let mut result = [0_u16; 4];
    for (index, component) in version.split('.').enumerate() {
        result[index] = component
            .parse()
            .map_err(|_| String::from("package version is noncanonical"))?;
    }
    Ok(result)
}

fn is_canonical_package_version(version: &str) -> bool {
    let components = version.split('.').collect::<Vec<_>>();
    components.len() == 4
        && components.iter().all(|component| {
            !component.is_empty()
                && component.bytes().all(|value| value.is_ascii_digit())
                && (component == &"0" || !component.starts_with('0'))
                && component.parse::<u16>().is_ok()
        })
}

pub(crate) fn validate_geodata_manifest(
    manifest_bytes: &[u8],
    trusted_files: &BTreeMap<String, (u64, String)>,
) -> Result<(), String> {
    let manifest: GeoDataManifest = serde_json::from_slice(manifest_bytes)
        .map_err(|error| format!("parse GeoData manifest failed: {error}"))?;
    if manifest.schema_version != 1 || manifest.files.len() != REQUIRED_GEODATA_ASSETS.len() {
        return Err(String::from("GeoData manifest has an unsupported shape"));
    }

    let mut seen_names = BTreeSet::new();
    for asset in manifest.files {
        let Some((_, package_path)) = REQUIRED_GEODATA_ASSETS
            .iter()
            .find(|(name, _)| *name == asset.name)
        else {
            return Err(format!(
                "GeoData manifest contains an unexpected asset: {}",
                asset.name
            ));
        };
        if !seen_names.insert(asset.name.clone()) {
            return Err(format!(
                "GeoData manifest contains a duplicate asset: {}",
                asset.name
            ));
        }
        if asset.length == 0 || asset.length > MAX_GEODATA_ASSET_BYTES {
            return Err(format!(
                "GeoData manifest asset length is invalid: {}",
                asset.name
            ));
        }
        if asset.sha256.len() != 64
            || !asset
                .sha256
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(format!(
                "GeoData manifest SHA-256 is noncanonical: {}",
                asset.name
            ));
        }

        let Some((actual_length, actual_sha256)) = trusted_files.get(*package_path) else {
            return Err(format!(
                "GeoData manifest asset is missing from MSIX: {}",
                asset.name
            ));
        };
        if *actual_length != asset.length || actual_sha256 != &asset.sha256 {
            return Err(format!(
                "GeoData manifest asset does not match MSIX content: {}",
                asset.name
            ));
        }
    }

    if seen_names.len() != REQUIRED_GEODATA_ASSETS.len() {
        return Err(String::from("GeoData manifest asset set is incomplete"));
    }
    Ok(())
}

fn unavailable_anchor(source_manifest: &Path) -> Result<String, String> {
    let manifest = fs::read_to_string(source_manifest)
        .map_err(|error| format!("read source Appx manifest failed: {error}"))?;
    let identity = parse_source_appx_identity(&manifest)?;
    let mut source = String::from("pub const TRUST_ANCHOR_AVAILABLE: bool = false;\n");
    source.push_str("pub const TRUSTED_MSIX_SHA256: &str = \"\";\n");
    write_package_identity_constants(&mut source, &identity);
    source.push_str("pub const TRUSTED_CERTIFICATE_SHA256: &str = \"\";\n");
    source.push_str("pub const TRUSTED_PRIMARY_MSIX_RELATIVE_PATH: &str = \"\";\n");
    source.push_str("pub const TRUSTED_CERTIFICATE_RELATIVE_PATH: &str = \"\";\n");
    source.push_str("pub const TRUSTED_PAYLOAD_FILES: &[(&str, u64, &str)] = &[];\n");
    source.push_str("pub const TRUSTED_ARCHIVE_FILES: &[(&str, u64, &str)] = &[];\n");
    source.push_str("pub const TRUSTED_REGISTERED_PACKAGE_FILES: &[(&str, u64, &str)] = &[];\n");
    source.push_str("pub const TRUSTED_MACHINE_FILES: &[(&str, u64, &str)] = &[];\n");
    Ok(source)
}

fn enumerate_payload_files(payload: &Path) -> Result<BTreeMap<String, (u64, String)>, String> {
    let mut files = BTreeMap::new();
    enumerate_payload_directory(payload, payload, &mut files)?;
    if files.is_empty() {
        return Err(String::from("payload file manifest is empty"));
    }
    Ok(files)
}

fn ensure_payload_root_is_ordinary(payload: &Path) -> Result<(), String> {
    if payload.components().any(|component| {
        matches!(
            component,
            std::path::Component::CurDir | std::path::Component::ParentDir
        )
    }) {
        return Err(String::from(
            "payload path must be lexically absolute and clean",
        ));
    }
    let absolute = if payload.is_absolute() {
        payload.to_path_buf()
    } else {
        env::current_dir()
            .map_err(|error| format!("read current directory failed: {error}"))?
            .join(payload)
    };
    for ancestor in absolute.ancestors() {
        let metadata = match fs::symlink_metadata(ancestor) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(error) => {
                return Err(format!(
                    "read payload ancestor metadata failed for {}: {error}",
                    ancestor.display()
                ));
            }
        };
        if metadata_is_reparse_point(&metadata) {
            return Err(format!(
                "payload path traverses a reparse point: {}",
                ancestor.display()
            ));
        }
        if ancestor == absolute && !metadata.is_dir() {
            return Err(String::from("payload root is not an ordinary directory"));
        }
    }
    Ok(())
}

fn enumerate_payload_directory(
    root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, (u64, String)>,
) -> Result<(), String> {
    for entry in fs::read_dir(directory)
        .map_err(|error| format!("read payload directory failed: {error}"))?
    {
        let entry = entry.map_err(|error| format!("read payload entry failed: {error}"))?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|error| format!("read payload metadata failed: {error}"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(format!(
                "payload reparse point rejected: {}",
                path.display()
            ));
        }
        if metadata.is_dir() {
            let relative = relative_payload_path(root, &path)?;
            if !matches!(relative.as_str(), "dependencies" | "dependencies/x64") {
                return Err(format!("unexpected release payload directory: {relative}"));
            }
            enumerate_payload_directory(root, &path, files)?;
            continue;
        }
        if !metadata.is_file() {
            return Err(format!("payload path kind rejected: {}", path.display()));
        }
        let relative = relative_payload_path(root, &path)?;
        if metadata.len() == 0 || metadata.len() > MAX_TRUSTED_FILE_BYTES {
            return Err(format!(
                "release payload file length is invalid: {relative}"
            ));
        }
        let dependency_name = relative.strip_prefix("dependencies/x64/");
        let allowed = relative == "clashsharp_temporarykey.cer"
            || relative == PAYLOAD_PROVENANCE_PATH
            || (!relative.contains('/') && relative.ends_with(".msix"))
            || dependency_name.is_some_and(|name| {
                !name.is_empty() && !name.contains('/') && name.ends_with(".msix")
            });
        if !allowed {
            return Err(format!("unexpected release payload file: {relative}"));
        }
        let hash = hash_file(&path)?;
        if files
            .insert(relative.clone(), (metadata.len(), hash))
            .is_some()
        {
            return Err(format!("case-colliding release payload file: {relative}"));
        }
    }
    Ok(())
}

fn metadata_is_reparse_point(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;

        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
        metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

fn relative_payload_path(root: &Path, path: &Path) -> Result<String, String> {
    let relative = path
        .strip_prefix(root)
        .map_err(|_| String::from("payload path escaped fixed root"))?;
    let mut components = Vec::new();
    for component in relative.components() {
        let std::path::Component::Normal(value) = component else {
            return Err(String::from("payload relative path is invalid"));
        };
        let value = value
            .to_str()
            .ok_or_else(|| String::from("payload relative path is not UTF-8"))?;
        components.push(value.to_ascii_lowercase());
    }
    Ok(components.join("/"))
}

fn hash_file(path: &Path) -> Result<String, String> {
    let mut file =
        File::open(path).map_err(|error| format!("open {} failed: {error}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file
            .read(&mut buffer)
            .map_err(|error| format!("read {} failed: {error}", path.display()))?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(lower_hex(&hasher.finalize()))
}

fn lower_hex(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(result, "{byte:02x}").unwrap();
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write as _;
    use std::sync::atomic::{AtomicU64, Ordering};

    static ARCHIVE_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct TemporaryArchive(PathBuf);

    impl Drop for TemporaryArchive {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.0);
        }
    }

    fn final_manifest(identity_attributes: &str, application_attributes: &str) -> String {
        format!(
            r#"<?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="{APPX_MANIFEST_NAMESPACE}" xmlns:uap10="{APPX_UAP10_NAMESPACE}">
              <!-- <Identity Version="9.9.9.9" /> -->
              <Identity {identity_attributes} />
              <Properties>
                <uap10:PackageIntegrity><uap10:Content Enforcement="on" /></uap10:PackageIntegrity>
              </Properties>
              <Dependencies>
                <PackageDependency Name="{EXPECTED_DEPENDENCY_NAME}" Publisher="{EXPECTED_DEPENDENCY_PUBLISHER}" MinVersion="8000.806.2252.0" />
              </Dependencies>
              <Applications><Application {application_attributes} /></Applications>
            </Package>"#
        )
    }

    fn dependency_manifest(asset: &str) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "runtimeTarget": { "name": "net10.0/win-x64" },
            "targets": {
                "net10.0/win-x64": {
                    "ClashSharp.Contract/1.0.0": { "runtime": { (asset): {} } }
                }
            }
        }))
        .unwrap()
    }

    fn create_final_file_contract_archive(
        extra: Option<&str>,
        omitted: Option<&str>,
    ) -> TemporaryArchive {
        let sequence = ARCHIVE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = env::temp_dir().join(format!(
            "clashsharp-installer-file-contract-{}-{sequence}.msix",
            std::process::id()
        ));
        let file = File::create(&path).unwrap();
        let mut writer = zip::ZipWriter::new(file);
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Stored);
        let mut names = REQUIRED_PACKAGE_FILES
            .into_iter()
            .chain(REQUIRED_PACKAGE_ASSETS)
            .chain(ALLOWED_GEODATA_PATHS)
            .collect::<BTreeSet<_>>();
        if let Some(omitted) = omitted {
            names.remove(omitted);
        }
        if let Some(extra) = extra {
            names.insert(extra);
        }
        for name in names {
            writer.start_file(name, options).unwrap();
            let content = match name {
                "ClashSharp.deps.json" => dependency_manifest("ClashSharp.dll"),
                "ClashSharp.RecoveryWatchdog.deps.json" => {
                    dependency_manifest("ClashSharp.RecoveryWatchdog.dll")
                }
                "Binaries/Service/ClashSharp.MihomoService.deps.json" => {
                    dependency_manifest("ClashSharp.MihomoService.dll")
                }
                _ => vec![b'x'],
            };
            writer.write_all(&content).unwrap();
        }
        writer.finish().unwrap();
        TemporaryArchive(path)
    }

    #[test]
    fn final_appx_identity_is_complete_and_derived_from_manifest() {
        let manifest = final_manifest(
            r#"Name="67dc1dc3-13fd-46c5-84f4-2932d94b566f" Publisher="CN=linzh" Version="1.2.3.4" ProcessorArchitecture="x64""#,
            r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#,
        );
        let identity = parse_final_appx_identity(&manifest).unwrap();

        assert_eq!(identity.version, "1.2.3.4");
        assert_eq!(identity.publisher_id, "vj7sjtzkt239a");
        assert_eq!(
            identity.family_name,
            "67dc1dc3-13fd-46c5-84f4-2932d94b566f_vj7sjtzkt239a"
        );
        assert_eq!(identity.architecture, "x64");
        assert_eq!(identity.application_id, "App");
        assert_eq!(identity.application_executable, "ClashSharp.exe");
    }

    #[test]
    fn appx_identity_parser_rejects_noncanonical_versions() {
        for version in ["1.2.3", "01.2.3.4", "1.2.3.65536", "1.2.3.four"] {
            let manifest = final_manifest(
                &format!(
                    r#"Name="ClashSharp" Publisher="CN=linzh" Version="{version}" ProcessorArchitecture="x64""#
                ),
                r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#,
            );
            assert!(
                parse_final_appx_identity(&manifest).is_err(),
                "accepted {version}"
            );
        }
    }

    #[test]
    fn publisher_replacement_derives_a_new_windows_family_without_manual_constants() {
        let publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        let manifest = final_manifest(
            &format!(
                r#"Name="ClashSharp.Product" Publisher="{publisher}" Version="2.0.0.0" ProcessorArchitecture="x64""#
            ),
            r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#,
        );
        let identity = parse_final_appx_identity(&manifest).unwrap();

        assert_eq!(identity.publisher_id, "8wekyb3d8bbwe");
        assert_eq!(identity.family_name, "ClashSharp.Product_8wekyb3d8bbwe");
    }

    #[test]
    fn final_appx_contract_rejects_wrong_architecture_or_executable() {
        let identity = r#"Name="ClashSharp" Publisher="CN=linzh" Version="1.0.0.0" ProcessorArchitecture="arm64""#;
        let application =
            r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#;
        assert!(parse_final_appx_identity(&final_manifest(identity, application)).is_err());

        let identity = r#"Name="ClashSharp" Publisher="CN=linzh" Version="1.0.0.0" ProcessorArchitecture="x64""#;
        let application =
            r#"Id="App" Executable="Other.exe" EntryPoint="Windows.FullTrustApplication""#;
        assert!(parse_final_appx_identity(&final_manifest(identity, application)).is_err());
    }

    #[test]
    fn final_appx_contract_requires_package_integrity_enforcement() {
        let identity = r#"Name="ClashSharp" Publisher="CN=linzh" Version="1.0.0.0" ProcessorArchitecture="x64""#;
        let application =
            r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#;
        let exact = final_manifest(identity, application);
        assert!(parse_final_appx_manifest(&exact).is_ok());
        assert!(
            parse_final_appx_manifest(&exact.replace("<uap10:Content Enforcement=\"on\" />", ""))
                .is_err()
        );
        assert!(
            parse_final_appx_manifest(&exact.replace("Enforcement=\"on\"", "Enforcement=\"off\""))
                .is_err()
        );
    }

    #[test]
    fn final_appx_contract_requires_one_exact_framework_dependency() {
        let identity = r#"Name="ClashSharp" Publisher="CN=linzh" Version="1.0.0.0" ProcessorArchitecture="x64""#;
        let application =
            r#"Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication""#;
        let exact = final_manifest(identity, application);
        assert!(parse_final_appx_manifest(&exact).is_ok());

        let wrong_name = exact.replace(EXPECTED_DEPENDENCY_NAME, "Microsoft.Other.Framework");
        assert!(parse_final_appx_manifest(&wrong_name).is_err());
        let wrong_publisher = exact.replace(EXPECTED_DEPENDENCY_PUBLISHER, "CN=Other");
        assert!(parse_final_appx_manifest(&wrong_publisher).is_err());
        let duplicated = exact.replace(
            "</Dependencies>",
            &format!(
                r#"<PackageDependency Name="{EXPECTED_DEPENDENCY_NAME}" Publisher="{EXPECTED_DEPENDENCY_PUBLISHER}" MinVersion="8000.806.2252.0" /></Dependencies>"#,
            ),
        );
        assert!(parse_final_appx_manifest(&duplicated).is_err());
    }

    #[test]
    fn dependency_identity_requires_x64_framework_package() {
        let manifest = |architecture: &str, framework: &str| {
            format!(
                r#"<Package xmlns="{APPX_MANIFEST_NAMESPACE}">
                    <Identity Name="{EXPECTED_DEPENDENCY_NAME}" Publisher="{EXPECTED_DEPENDENCY_PUBLISHER}" Version="8000.900.1.0" ProcessorArchitecture="{architecture}" />
                    <Properties><Framework>{framework}</Framework></Properties>
                </Package>"#,
            )
        };
        let identity = parse_dependency_package_identity(&manifest("x64", "true")).unwrap();
        assert_eq!(identity.name, EXPECTED_DEPENDENCY_NAME);
        assert_eq!(identity.version, "8000.900.1.0");
        assert!(parse_dependency_package_identity(&manifest("arm64", "true")).is_err());
        assert!(parse_dependency_package_identity(&manifest("x64", "false")).is_err());
    }

    #[test]
    fn final_file_contract_rejects_probes_installers_and_extra_executables() {
        for forbidden in [
            "ClashSharp.ProcessProbe.exe",
            "SandboxTest.dll",
            "Binaries/Installer.exe",
            "SecondUpdater.exe",
            "ClashSharp.pdb",
            "packages.lock.json",
        ] {
            assert!(
                validate_final_msix_entry_name(forbidden).is_err(),
                "accepted {forbidden}"
            );
        }
        assert!(validate_final_msix_entry_name("Microsoft.Extensions.Hosting.dll").is_ok());

        let exact = REQUIRED_PACKAGE_EXECUTABLES
            .into_iter()
            .map(str::to_owned)
            .collect::<BTreeSet<_>>();
        assert!(validate_final_msix_executable_set(&exact).is_ok());
        let mut extra = exact;
        extra.insert(String::from("SecondProduct.exe"));
        assert!(validate_final_msix_executable_set(&extra).is_err());
    }

    #[test]
    fn final_archive_file_contract_is_exact_and_rejects_missing_or_extra_files() {
        let exact = create_final_file_contract_archive(None, None);
        let file = File::open(&exact.0).unwrap();
        let mut archive = ZipArchive::new(file).unwrap();
        assert!(validate_final_msix_file_contract(&mut archive).is_ok());

        let extra = create_final_file_contract_archive(Some("Arbitrary.dll"), None);
        let file = File::open(&extra.0).unwrap();
        let mut archive = ZipArchive::new(file).unwrap();
        let error = validate_final_msix_file_contract(&mut archive).unwrap_err();
        assert!(error.contains("outside the allowlist"));

        let missing = create_final_file_contract_archive(
            None,
            Some("ClashSharp.RecoveryWatchdog.runtimeconfig.json"),
        );
        let file = File::open(&missing.0).unwrap();
        let mut archive = ZipArchive::new(file).unwrap();
        let error = validate_final_msix_file_contract(&mut archive).unwrap_err();
        assert!(error.contains("required final MSIX file is missing"));
    }

    #[test]
    fn registered_package_manifest_excludes_only_non_hash_stable_deployment_files() {
        for deployment_managed in NON_HASH_STABLE_REGISTERED_MSIX_FILES {
            assert!(is_non_hash_stable_registered_msix_file(deployment_managed));
        }
        assert!(!is_non_hash_stable_registered_msix_file("AppxManifest.xml"));
        assert!(!is_non_hash_stable_registered_msix_file("ClashSharp.exe"));
        assert!(!is_non_hash_stable_registered_msix_file(
            "Binaries/Service/ClashSharp.MihomoService.exe"
        ));
    }

    #[test]
    fn block_map_payload_manifest_is_canonical_and_exact() {
        let block_map = br#"<?xml version="1.0" encoding="UTF-8"?>
            <BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap"
                HashMethod="http://www.w3.org/2001/04/xmlenc#sha256">
                <File Name="AppxManifest.xml" Size="10"><Block Hash="AA==" /></File>
                <File Name="Binaries\Service\Host.exe" Size="20"><Block Hash="AA==" /></File>
            </BlockMap>"#;
        let parsed = parse_appx_block_map_file_manifest(block_map).unwrap();
        assert_eq!(parsed.get("appxmanifest.xml"), Some(&10));
        assert_eq!(parsed.get("binaries/service/host.exe"), Some(&20));

        let registered = BTreeMap::from([
            ("appxmanifest.xml".to_owned(), (10, "a".repeat(64))),
            ("binaries/service/host.exe".to_owned(), (20, "b".repeat(64))),
        ]);
        assert!(validate_registered_manifest_matches_block_map(&registered, &parsed).is_ok());
        assert!(
            parse_appx_block_map_file_manifest(
                block_map
                    .as_slice()
                    .strip_suffix(b"</BlockMap>")
                    .unwrap_or(block_map)
            )
            .is_err()
        );
        assert!(normalize_block_map_file_name(r"Binaries\..\evil.exe").is_err());
        assert!(normalize_block_map_file_name("Encoded%21Name.dll").is_err());
    }

    #[test]
    fn dotnet_dependency_allowlist_accepts_libraries_but_rejects_scripts() {
        let document = |asset: &str| {
            serde_json::to_vec(&serde_json::json!({
                "runtimeTarget": { "name": "net10.0/win-x64" },
                "targets": {
                    "net10.0/win-x64": {
                        "Example/1.0.0": { "runtime": { (asset): {} } }
                    }
                }
            }))
            .unwrap()
        };
        let mut allowed = BTreeSet::new();
        assert!(add_dotnet_dependency_assets(&document("Example.dll"), "", &mut allowed).is_ok());
        assert!(allowed.contains("Example.dll"));
        assert!(
            add_dotnet_dependency_assets(&document("post-install.ps1"), "", &mut allowed).is_err()
        );
    }

    #[test]
    fn provenance_primitives_reject_noncanonical_paths_thumbprints_and_versions() {
        assert_eq!(
            normalize_provenance_path("Dependencies/x64/Runtime.msix").unwrap(),
            "dependencies/x64/runtime.msix"
        );
        assert!(normalize_provenance_path("Dependencies/../Runtime.msix").is_err());
        assert!(is_canonical_thumbprint(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        ));
        assert!(!is_canonical_thumbprint(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        ));
        assert_eq!(
            compare_package_versions("8000.900.0.0", "8000.806.2252.0").unwrap(),
            std::cmp::Ordering::Greater
        );
        assert!(compare_package_versions("8000.0900.0.0", "8000.806.2252.0").is_err());
        assert!(ensure_payload_root_is_ordinary(Path::new("../payload")).is_err());
    }
}
