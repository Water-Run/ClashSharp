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
const MAX_GEODATA_ASSET_BYTES: u64 = 256 * 1024 * 1024;
const MAX_GEODATA_MANIFEST_BYTES: u64 = 64 * 1024;
const MAX_APPX_MANIFEST_BYTES: u64 = 1024 * 1024;
const APPX_MANIFEST_PATH: &str = "AppxManifest.xml";
const APPX_MANIFEST_NAMESPACE: &str =
    "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
const EXPECTED_PACKAGE_ARCHITECTURE: &str = "x64";
const EXPECTED_APPLICATION_ID: &str = "App";
const EXPECTED_APPLICATION_EXECUTABLE: &str = "ClashSharp.exe";
const EXPECTED_APPLICATION_ENTRY_POINT: &str = "Windows.FullTrustApplication";
const SOURCE_APPLICATION_EXECUTABLE: &str = "$targetnametoken$.exe";
const SOURCE_APPLICATION_ENTRY_POINT: &str = "$targetentrypoint$";
const GEODATA_MANIFEST_PATH: &str = "Binaries/GeoData/manifest.json";
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

#[cfg(not(test))]
fn main() {
    println!("cargo:rerun-if-changed=payload");
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
    match generate_payload_trust_anchor(Path::new("payload")) {
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
    let package_identity = extract_trusted_package_identity(&mut archive)?;
    let mut trusted_files = BTreeMap::<String, (u64, String)>::new();
    let mut geodata_manifest_bytes = None;
    let mut total_bytes = 0_u64;

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
        if !trusted {
            continue;
        }
        if entry.is_dir()
            || name.ends_with('/')
            || name.contains('\\')
            || entry.enclosed_name().is_none()
            || entry
                .unix_mode()
                .is_some_and(|mode| mode & 0o170_000 == 0o120_000)
        {
            return Err(format!("unsafe trusted MSIX entry: {name}"));
        }
        if entry.size() == 0 || entry.size() > MAX_TRUSTED_FILE_BYTES {
            return Err(format!("trusted MSIX entry has invalid length: {name}"));
        }
        let capture_geodata_manifest = name == GEODATA_MANIFEST_PATH;
        if capture_geodata_manifest && entry.size() > MAX_GEODATA_MANIFEST_BYTES {
            return Err(String::from("GeoData manifest exceeds its size budget"));
        }
        total_bytes = total_bytes
            .checked_add(entry.size())
            .ok_or_else(|| String::from("trusted payload length overflow"))?;
        if total_bytes > MAX_TRUSTED_TOTAL_BYTES {
            return Err(String::from("trusted machine payload exceeds size budget"));
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
                .map_err(|error| format!("read trusted MSIX entry failed: {error}"))?;
            if count == 0 {
                break;
            }
            actual_length = actual_length
                .checked_add(count as u64)
                .ok_or_else(|| String::from("trusted entry length overflow"))?;
            hasher.update(&buffer[..count]);
            if capture_geodata_manifest {
                captured_bytes.extend_from_slice(&buffer[..count]);
            }
        }
        if actual_length != entry.size() {
            return Err(format!("trusted MSIX entry length changed: {name}"));
        }
        let key = name.to_ascii_lowercase();
        if trusted_files
            .insert(key, (actual_length, lower_hex(&hasher.finalize())))
            .is_some()
        {
            return Err(format!("case-colliding trusted MSIX entry: {name}"));
        }
        if capture_geodata_manifest && geodata_manifest_bytes.replace(captured_bytes).is_some() {
            return Err(String::from("duplicate GeoData manifest MSIX entry"));
        }
    }

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
    write_package_identity_constants(&mut source, &package_identity);
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
    source.push_str("pub const TRUSTED_MACHINE_FILES: &[(&str, u64, &str)] = &[\n");
    for (path, (length, hash)) in trusted_files {
        writeln!(source, "    ({path:?}, {length}, \"{hash}\"),").unwrap();
    }
    source.push_str("];\n");
    Ok(source)
}

fn extract_trusted_package_identity(
    archive: &mut ZipArchive<File>,
) -> Result<AppxPackageIdentity, String> {
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
    let manifest = std::str::from_utf8(bytes)
        .map_err(|_| String::from("AppxManifest.xml is not canonical UTF-8"))?;
    parse_final_appx_identity(manifest)
}

fn parse_final_appx_identity(manifest: &str) -> Result<AppxPackageIdentity, String> {
    let document = parse_appx_document(manifest)?;
    let package = canonical_package_element(&document)?;
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

    complete_package_identity(
        manifest_identity,
        architecture,
        application_id,
        application_executable,
        application_entry_point,
    )
}

fn parse_source_appx_identity(manifest: &str) -> Result<AppxPackageIdentity, String> {
    let document = parse_appx_document(manifest)?;
    let package = canonical_package_element(&document)?;
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
        if metadata.file_type().is_symlink() {
            return Err(format!("payload symlink rejected: {}", path.display()));
        }
        if metadata.is_dir() {
            enumerate_payload_directory(root, &path, files)?;
            continue;
        }
        if !metadata.is_file() {
            return Err(format!("payload path kind rejected: {}", path.display()));
        }
        let relative = relative_payload_path(root, &path)?;
        if relative == ".gitkeep" {
            continue;
        }
        let allowed = relative == "clashsharp_temporarykey.cer"
            || (!relative.contains('/') && relative.ends_with(".msix"))
            || (relative.starts_with("dependencies/") && relative.ends_with(".msix"));
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

    fn final_manifest(identity_attributes: &str, application_attributes: &str) -> String {
        format!(
            r#"<?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="{APPX_MANIFEST_NAMESPACE}">
              <!-- <Identity Version="9.9.9.9" /> -->
              <Identity {identity_attributes} />
              <Applications><Application {application_attributes} /></Applications>
            </Package>"#
        )
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
}
