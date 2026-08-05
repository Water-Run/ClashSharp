//! Build-time payload trust anchor used on both sides of the narrow UAC boundary.

use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

include!(concat!(env!("OUT_DIR"), "/payload_trust_anchor.rs"));

const SERVICE_PREFIX: &str = "binaries/service/";
const MIHOMO_PATH: &str = "binaries/mihomo.exe";
const GEODATA_PREFIX: &str = "binaries/geodata/";

/// Paths from one complete sibling payload after exact-set and hash verification.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifiedInstallerPayload {
    primary_msix: PathBuf,
    certificate: PathBuf,
    dependencies: Vec<PathBuf>,
}

impl VerifiedInstallerPayload {
    /// Gets the only primary MSIX anchored into this executable.
    #[must_use]
    pub fn primary_msix(&self) -> &Path {
        &self.primary_msix
    }

    /// Gets the anchored public signing certificate.
    #[must_use]
    pub fn certificate(&self) -> &Path {
        &self.certificate
    }

    /// Gets the exact anchored dependency package set.
    #[must_use]
    pub fn dependencies(&self) -> &[PathBuf] {
        &self.dependencies
    }
}

/// Verifies the exact sibling payload set against hashes embedded in this executable.
pub fn verify_installer_payload(payload_root: &Path) -> Result<VerifiedInstallerPayload, String> {
    ensure_anchor_available()?;
    let actual = enumerate_payload_files(payload_root)?;
    if actual.len() != TRUSTED_PAYLOAD_FILES.len() {
        return Err(String::from("installer.trust.payload_file_set_invalid"));
    }
    for (relative_path, expected_length, expected_hash) in TRUSTED_PAYLOAD_FILES {
        let Some(path) = actual.get(*relative_path) else {
            return Err(format!(
                "installer.trust.payload_file_missing: {relative_path}"
            ));
        };
        verify_file(
            path,
            expected_hash,
            Some(*expected_length),
            "installer.trust.payload_file_invalid",
        )?;
    }

    let primary_msix = actual
        .get(TRUSTED_PRIMARY_MSIX_RELATIVE_PATH)
        .cloned()
        .ok_or_else(|| String::from("installer.trust.msix_invalid"))?;
    let certificate = actual
        .get(TRUSTED_CERTIFICATE_RELATIVE_PATH)
        .cloned()
        .ok_or_else(|| String::from("installer.trust.certificate_invalid"))?;
    verify_file(
        &primary_msix,
        TRUSTED_MSIX_SHA256,
        None,
        "installer.trust.msix_invalid",
    )?;
    verify_file(
        &certificate,
        TRUSTED_CERTIFICATE_SHA256,
        None,
        "installer.trust.certificate_invalid",
    )?;
    let dependencies = actual
        .iter()
        .filter(|(path, _)| path.starts_with("dependencies/") && path.ends_with(".msix"))
        .map(|(_, path)| path.clone())
        .collect();
    Ok(VerifiedInstallerPayload {
        primary_msix,
        certificate,
        dependencies,
    })
}

/// Serializes the embedded machine-file manifest for protected post-copy verification.
pub fn trusted_machine_manifest_json() -> Result<String, String> {
    let entries = TRUSTED_MACHINE_FILES
        .iter()
        .map(|(relative_path, length, sha256)| {
            serde_json::json!({
                "path": relative_path,
                "length": length,
                "sha256": sha256,
            })
        })
        .collect::<Vec<_>>();
    serde_json::to_string(&entries).map_err(|_| String::from("installer.trust.manifest_invalid"))
}

/// Gets the canonical whole-file hash embedded for the primary MSIX.
pub fn trusted_msix_sha256() -> Result<&'static str, String> {
    ensure_anchor_available()?;
    Ok(TRUSTED_MSIX_SHA256)
}

/// Gets the canonical four-component package version embedded from the trusted MSIX manifest.
pub fn trusted_package_version() -> Result<&'static str, String> {
    ensure_anchor_available()?;
    Ok(TRUSTED_PACKAGE_VERSION)
}

/// Verifies the complete machine-executable subset of one registered package.
pub fn verify_registered_machine_payload(install_root: &Path) -> Result<(), String> {
    ensure_anchor_available()?;
    let actual = enumerate_machine_files(install_root)?;
    if actual.len() != TRUSTED_MACHINE_FILES.len() {
        return Err(String::from("installer.trust.machine_file_set_invalid"));
    }

    for (relative_path, expected_length, expected_hash) in TRUSTED_MACHINE_FILES {
        let normalized = relative_path.to_ascii_lowercase();
        let Some(path) = actual.get(&normalized) else {
            return Err(format!(
                "installer.trust.machine_file_missing: {relative_path}"
            ));
        };
        verify_file(
            path,
            expected_hash,
            Some(*expected_length),
            "installer.trust.machine_file_invalid",
        )?;
    }
    Ok(())
}

fn ensure_anchor_available() -> Result<(), String> {
    if !TRUST_ANCHOR_AVAILABLE
        || TRUSTED_MSIX_SHA256.len() != 64
        || !is_canonical_package_version(TRUSTED_PACKAGE_VERSION)
        || TRUSTED_CERTIFICATE_SHA256.len() != 64
        || TRUSTED_PAYLOAD_FILES.is_empty()
        || TRUSTED_MACHINE_FILES.is_empty()
    {
        return Err(String::from("installer.trust.anchor_unavailable"));
    }
    Ok(())
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

fn enumerate_payload_files(payload_root: &Path) -> Result<BTreeMap<String, PathBuf>, String> {
    let root_metadata = fs::symlink_metadata(payload_root)
        .map_err(|_| String::from("installer.trust.payload_root_unavailable"))?;
    if !root_metadata.is_dir() || metadata_is_reparse_point(&root_metadata) {
        return Err(String::from("installer.trust.payload_root_unsafe"));
    }
    let mut result = BTreeMap::new();
    enumerate_payload_directory(payload_root, payload_root, &mut result)?;
    result.remove(".gitkeep");
    Ok(result)
}

fn enumerate_payload_directory(
    payload_root: &Path,
    directory: &Path,
    result: &mut BTreeMap<String, PathBuf>,
) -> Result<(), String> {
    for entry in fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?
    {
        let entry =
            entry.map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.payload_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.payload_reparse_rejected"));
        }
        if metadata.is_dir() {
            enumerate_payload_directory(payload_root, &path, result)?;
            continue;
        }
        if !metadata.is_file() {
            return Err(String::from("installer.trust.payload_path_kind_invalid"));
        }
        let relative = normalize_relative_path(
            path.strip_prefix(payload_root)
                .map_err(|_| String::from("installer.trust.payload_path_escaped"))?,
        )?;
        if result.insert(relative, path).is_some() {
            return Err(String::from("installer.trust.payload_path_collision"));
        }
    }
    Ok(())
}

fn enumerate_machine_files(install_root: &Path) -> Result<BTreeMap<String, PathBuf>, String> {
    let root_metadata = fs::symlink_metadata(install_root)
        .map_err(|_| String::from("installer.trust.install_root_unavailable"))?;
    if !root_metadata.is_dir() || metadata_is_reparse_point(&root_metadata) {
        return Err(String::from("installer.trust.install_root_unsafe"));
    }

    let mut result = BTreeMap::new();
    enumerate_directory(install_root, install_root, &mut result)?;
    for required in [
        MIHOMO_PATH,
        "binaries/service/clashsharp.mihomoservice.exe",
        "binaries/geodata/manifest.json",
    ] {
        if !result.contains_key(required) {
            return Err(format!("installer.trust.machine_file_missing: {required}"));
        }
    }
    Ok(result)
}

fn enumerate_directory(
    install_root: &Path,
    directory: &Path,
    result: &mut BTreeMap<String, PathBuf>,
) -> Result<(), String> {
    let mut entries = fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.machine_directory_unreadable"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| String::from("installer.trust.machine_directory_unreadable"))?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.machine_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.machine_reparse_rejected"));
        }

        let relative = path
            .strip_prefix(install_root)
            .map_err(|_| String::from("installer.trust.machine_path_escaped"))?;
        let normalized = normalize_relative_path(relative)?;
        let trusted_scope = normalized == MIHOMO_PATH
            || normalized.starts_with(SERVICE_PREFIX)
            || normalized.starts_with(GEODATA_PREFIX);
        if metadata.is_dir() {
            if trusted_scope
                || matches!(
                    normalized.as_str(),
                    "binaries" | "binaries/service" | "binaries/geodata"
                )
            {
                enumerate_directory(install_root, &path, result)?;
            }
            continue;
        }
        if !trusted_scope {
            continue;
        }
        if !metadata.is_file() {
            return Err(String::from("installer.trust.machine_path_kind_invalid"));
        }
        if result.insert(normalized, path).is_some() {
            return Err(String::from("installer.trust.machine_path_collision"));
        }
    }
    Ok(())
}

fn normalize_relative_path(path: &Path) -> Result<String, String> {
    let mut segments = Vec::new();
    for component in path.components() {
        let std::path::Component::Normal(segment) = component else {
            return Err(String::from("installer.trust.machine_path_invalid"));
        };
        let segment = segment
            .to_str()
            .ok_or_else(|| String::from("installer.trust.machine_path_invalid"))?;
        if segment.is_empty() || segment.contains('/') || segment.contains('\\') {
            return Err(String::from("installer.trust.machine_path_invalid"));
        }
        segments.push(segment.to_ascii_lowercase());
    }
    Ok(segments.join("/"))
}

fn verify_file(
    path: &Path,
    expected_hash: &str,
    expected_length: Option<u64>,
    failure_code: &str,
) -> Result<(), String> {
    if expected_hash.len() != 64
        || !expected_hash
            .bytes()
            .all(|value| value.is_ascii_digit() || (b'a'..=b'f').contains(&value))
    {
        return Err(String::from("installer.trust.anchor_invalid"));
    }
    let metadata = fs::symlink_metadata(path).map_err(|_| failure_code.to_owned())?;
    if !metadata.is_file()
        || metadata_is_reparse_point(&metadata)
        || expected_length.is_some_and(|length| metadata.len() != length)
    {
        return Err(failure_code.to_owned());
    }

    let mut file = File::open(path).map_err(|_| failure_code.to_owned())?;
    let mut hasher = Sha256::new();
    let mut actual_length = 0_u64;
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file
            .read(&mut buffer)
            .map_err(|_| failure_code.to_owned())?;
        if count == 0 {
            break;
        }
        actual_length = actual_length
            .checked_add(count as u64)
            .ok_or_else(|| failure_code.to_owned())?;
        hasher.update(&buffer[..count]);
    }
    if expected_length.is_some_and(|length| actual_length != length)
        || lower_hex(&hasher.finalize()) != expected_hash
    {
        return Err(failure_code.to_owned());
    }
    Ok(())
}

fn lower_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        result.push(HEX[(byte >> 4) as usize] as char);
        result.push(HEX[(byte & 0x0f) as usize] as char);
    }
    result
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_anchor_is_canonical_or_deliberately_unavailable() {
        if TRUST_ANCHOR_AVAILABLE {
            assert_eq!(TRUSTED_MSIX_SHA256.len(), 64);
            assert!(is_canonical_package_version(TRUSTED_PACKAGE_VERSION));
            assert_eq!(trusted_package_version().unwrap(), TRUSTED_PACKAGE_VERSION);
            assert_eq!(TRUSTED_CERTIFICATE_SHA256.len(), 64);
            assert!(!TRUSTED_PAYLOAD_FILES.is_empty());
            assert!(!TRUSTED_MACHINE_FILES.is_empty());
        } else {
            assert!(verify_installer_payload(Path::new("missing")).is_err());
            assert!(trusted_package_version().is_err());
        }
    }

    #[test]
    fn package_version_canonicalization_is_strict() {
        for valid in ["0.0.0.0", "1.0.0.0", "65535.65535.65535.65535"] {
            assert!(is_canonical_package_version(valid), "rejected {valid}");
        }
        for invalid in [
            "",
            "1",
            "1.2.3",
            "1.2.3.4.5",
            "01.2.3.4",
            "1.-2.3.4",
            "1.2.3.65536",
        ] {
            assert!(!is_canonical_package_version(invalid), "accepted {invalid}");
        }
    }

    #[test]
    fn normalization_rejects_parent_components() {
        assert!(normalize_relative_path(Path::new(r"Binaries\..\evil.exe")).is_err());
        assert_eq!(
            normalize_relative_path(Path::new(r"Binaries\Service\Host.exe")).unwrap(),
            "binaries/service/host.exe"
        );
    }

    #[test]
    fn build_script_requires_and_parses_the_complete_geodata_set() {
        let build_script = include_str!("../build.rs");
        for required in [
            "Binaries/GeoData/manifest.json",
            "Binaries/GeoData/Country.mmdb",
            "Binaries/GeoData/GeoIP.dat",
            "Binaries/GeoData/GeoSite.dat",
            "Binaries/GeoData/ASN.mmdb",
        ] {
            assert!(build_script.contains(required));
        }
        assert!(build_script.contains("serde_json::from_slice"));
        assert!(build_script.contains("validate_geodata_manifest"));
        assert!(build_script.contains("actual_length != asset.length"));
        assert!(build_script.contains("actual_sha256 != &asset.sha256"));
    }
}
