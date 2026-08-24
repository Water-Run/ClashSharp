//! Build-time payload trust anchor used on both sides of the narrow UAC boundary.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

include!(concat!(env!("OUT_DIR"), "/payload_trust_anchor.rs"));

const DEPLOYED_FOOTPRINT_FILES: [&str; 3] = [
    "appxblockmap.xml",
    "appxmetadata/codeintegrity.cat",
    "appxsignature.p7x",
];
const SYSTEM_PACKAGE_METADATA_ROOT: &str = "microsoft.system.package.metadata";
const MAX_REGISTERED_PACKAGE_FILES: usize = 4096;
const MAX_DEPLOYMENT_METADATA_FILE_BYTES: u64 = 16 * 1024 * 1024;

/// Paths from one complete sibling payload after exact-set and hash verification.
#[must_use = "keep this guard alive while certificate and package consumers use its paths"]
#[derive(Debug)]
pub struct VerifiedInstallerPayload {
    payload_root: PathBuf,
    primary_msix: PathBuf,
    certificate: PathBuf,
    dependencies: Vec<PathBuf>,
    file_guards: BTreeMap<String, LockedFile>,
    directory_paths: BTreeSet<String>,
    _directory_guards: Vec<File>,
}

/// Locked, exact snapshot of one registered package while machine work consumes its identity.
#[must_use = "keep this guard alive until the machine transaction finishes"]
#[derive(Debug)]
pub struct VerifiedRegisteredPackage {
    install_root: PathBuf,
    expected_files: BTreeMap<String, (u64, String)>,
    file_guards: BTreeMap<String, LockedFile>,
    directory_paths: BTreeSet<String>,
    _directory_guards: Vec<File>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct FileIdentity {
    volume: u64,
    index: u64,
}

#[derive(Debug)]
struct LockedFile {
    path: PathBuf,
    handle: File,
    identity: FileIdentity,
    length: u64,
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

    /// Rehashes the same locked file objects and confirms every path still names that object.
    pub fn reverify(&mut self) -> Result<(), String> {
        let (current_files, current_directories) = enumerate_payload_paths(&self.payload_root)?;
        if current_files.len() != TRUSTED_PAYLOAD_FILES.len()
            || current_directories != self.directory_paths
        {
            return Err(String::from("installer.trust.payload_file_set_invalid"));
        }
        for (relative_path, expected_length, expected_hash) in TRUSTED_PAYLOAD_FILES {
            let current_path = current_files
                .get(*relative_path)
                .ok_or_else(|| format!("installer.trust.payload_file_missing: {relative_path}"))?;
            let locked = self
                .file_guards
                .get_mut(*relative_path)
                .ok_or_else(|| String::from("installer.trust.payload_lock_missing"))?;
            verify_locked_path_identity(
                current_path,
                locked,
                "installer.trust.payload_file_changed",
            )?;
            verify_open_file(
                &mut locked.handle,
                expected_hash,
                Some(*expected_length),
                "installer.trust.payload_file_invalid",
            )?;
        }
        Ok(())
    }
}

impl VerifiedRegisteredPackage {
    /// Rehashes all package-authored files and confirms the locked deployment tree is unchanged.
    pub fn reverify(&mut self) -> Result<(), String> {
        let (current_files, current_directories) =
            enumerate_registered_package_paths(&self.install_root)?;
        if current_files.len() != self.file_guards.len()
            || current_directories != self.directory_paths
            || current_files
                .keys()
                .any(|path| !self.file_guards.contains_key(path))
        {
            return Err(String::from("installer.trust.package_file_set_invalid"));
        }

        for (relative_path, locked) in &mut self.file_guards {
            let current_path = current_files
                .get(relative_path)
                .ok_or_else(|| String::from("installer.trust.package_file_missing"))?;
            verify_locked_path_identity(
                current_path,
                locked,
                "installer.trust.package_file_changed",
            )?;
            if let Some((expected_length, expected_hash)) = self.expected_files.get(relative_path) {
                verify_open_file(
                    &mut locked.handle,
                    expected_hash,
                    Some(*expected_length),
                    "installer.trust.package_file_invalid",
                )?;
            }
        }
        Ok(())
    }
}

/// Verifies the exact sibling payload set against hashes embedded in this executable.
pub fn verify_installer_payload(payload_root: &Path) -> Result<VerifiedInstallerPayload, String> {
    ensure_anchor_available()?;
    let payload_root = absolute_clean_path(payload_root)?;
    let mut directory_guards = lock_ordinary_directory_ancestors(&payload_root)?;
    let (mut actual, directory_paths) =
        enumerate_and_lock_payload_files(&payload_root, &mut directory_guards)?;
    if actual.len() != TRUSTED_PAYLOAD_FILES.len() {
        return Err(String::from("installer.trust.payload_file_set_invalid"));
    }
    if directory_paths
        != expected_parent_directories(TRUSTED_PAYLOAD_FILES.iter().map(|entry| entry.0))
    {
        return Err(String::from(
            "installer.trust.payload_directory_set_invalid",
        ));
    }
    for (relative_path, expected_length, expected_hash) in TRUSTED_PAYLOAD_FILES {
        let Some(file) = actual.get_mut(*relative_path) else {
            return Err(format!(
                "installer.trust.payload_file_missing: {relative_path}"
            ));
        };
        verify_open_file(
            &mut file.handle,
            expected_hash,
            Some(*expected_length),
            "installer.trust.payload_file_invalid",
        )?;
    }

    let primary_msix = actual
        .get(TRUSTED_PRIMARY_MSIX_RELATIVE_PATH)
        .map(|file| file.path.clone())
        .ok_or_else(|| String::from("installer.trust.msix_invalid"))?;
    let certificate = actual
        .get(TRUSTED_CERTIFICATE_RELATIVE_PATH)
        .map(|file| file.path.clone())
        .ok_or_else(|| String::from("installer.trust.certificate_invalid"))?;
    verify_open_file(
        &mut actual
            .get_mut(TRUSTED_PRIMARY_MSIX_RELATIVE_PATH)
            .ok_or_else(|| String::from("installer.trust.msix_invalid"))?
            .handle,
        TRUSTED_MSIX_SHA256,
        None,
        "installer.trust.msix_invalid",
    )?;
    verify_open_file(
        &mut actual
            .get_mut(TRUSTED_CERTIFICATE_RELATIVE_PATH)
            .ok_or_else(|| String::from("installer.trust.certificate_invalid"))?
            .handle,
        TRUSTED_CERTIFICATE_SHA256,
        None,
        "installer.trust.certificate_invalid",
    )?;
    let dependencies = actual
        .iter()
        .filter(|(path, _)| path.starts_with("dependencies/") && path.ends_with(".msix"))
        .map(|(_, file)| file.path.clone())
        .collect();
    Ok(VerifiedInstallerPayload {
        payload_root,
        primary_msix,
        certificate,
        dependencies,
        file_guards: actual,
        directory_paths,
        _directory_guards: directory_guards,
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

/// Gets the complete package identity generated from the trusted final MSIX manifest.
pub fn trusted_package_identity() -> Result<TrustedPackageIdentity, String> {
    ensure_anchor_available()?;
    Ok(TrustedPackageIdentity {
        name: TRUSTED_PACKAGE_IDENTITY_NAME,
        publisher: TRUSTED_PACKAGE_PUBLISHER,
        publisher_id: TRUSTED_PACKAGE_PUBLISHER_ID,
        family_name: TRUSTED_PACKAGE_FAMILY_NAME,
        version: TRUSTED_PACKAGE_VERSION,
        architecture: TRUSTED_PACKAGE_ARCHITECTURE,
        application_id: TRUSTED_APPLICATION_ID,
        application_executable: TRUSTED_APPLICATION_EXECUTABLE,
        application_entry_point: TRUSTED_APPLICATION_ENTRY_POINT,
    })
}

/// Complete immutable identity contract embedded from the final MSIX.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TrustedPackageIdentity {
    /// Package Identity Name.
    pub name: &'static str,
    /// Certificate-subject Publisher from the package manifest.
    pub publisher: &'static str,
    /// Windows-derived PublisherId.
    pub publisher_id: &'static str,
    /// Package family name derived from Name and PublisherId.
    pub family_name: &'static str,
    /// Canonical four-component package version.
    pub version: &'static str,
    /// Final package processor architecture.
    pub architecture: &'static str,
    /// Package-relative application identifier.
    pub application_id: &'static str,
    /// Final packaged application executable.
    pub application_executable: &'static str,
    /// Final packaged application entry point.
    pub application_entry_point: &'static str,
}

/// Verifies every file installed from the final MSIX, not only its machine-service subset.
pub fn verify_registered_package_payload(
    install_root: &Path,
) -> Result<VerifiedRegisteredPackage, String> {
    ensure_anchor_available()?;
    verify_registered_package_file_manifest(install_root, TRUSTED_REGISTERED_PACKAGE_FILES)
}

fn verify_registered_package_file_manifest(
    install_root: &Path,
    trusted_files: &[(&str, u64, &str)],
) -> Result<VerifiedRegisteredPackage, String> {
    let install_root = absolute_clean_path(install_root)?;
    let expected_files = owned_file_manifest(trusted_files)?;
    let mut directory_guards = Vec::new();
    let (mut actual, directory_paths) =
        enumerate_and_lock_registered_package_files(&install_root, &mut directory_guards)?;
    validate_registered_package_shape(&actual, &directory_paths, &expected_files)?;

    for (relative_path, (expected_length, expected_hash)) in &expected_files {
        let Some(file) = actual.get_mut(relative_path) else {
            return Err(format!(
                "installer.trust.package_file_missing: {relative_path}"
            ));
        };
        verify_open_file(
            &mut file.handle,
            expected_hash,
            Some(*expected_length),
            "installer.trust.package_file_invalid",
        )?;
    }

    Ok(VerifiedRegisteredPackage {
        install_root,
        expected_files,
        file_guards: actual,
        directory_paths,
        _directory_guards: directory_guards,
    })
}

fn ensure_anchor_available() -> Result<(), String> {
    if !TRUST_ANCHOR_AVAILABLE
        || TRUSTED_MSIX_SHA256.len() != 64
        || !package_identity_is_canonical()
        || !is_canonical_package_version(TRUSTED_PACKAGE_VERSION)
        || TRUSTED_CERTIFICATE_SHA256.len() != 64
        || TRUSTED_PAYLOAD_FILES.is_empty()
        || TRUSTED_ARCHIVE_FILES.is_empty()
        || TRUSTED_REGISTERED_PACKAGE_FILES.is_empty()
        || TRUSTED_MACHINE_FILES.is_empty()
    {
        return Err(String::from("installer.trust.anchor_unavailable"));
    }
    Ok(())
}

fn package_identity_is_canonical() -> bool {
    !TRUSTED_PACKAGE_IDENTITY_NAME.is_empty()
        && !TRUSTED_PACKAGE_PUBLISHER.is_empty()
        && TRUSTED_PACKAGE_PUBLISHER_ID.len() == 13
        && TRUSTED_PACKAGE_PUBLISHER_ID
            .bytes()
            .all(|value| b"0123456789abcdefghjkmnpqrstvwxyz".contains(&value))
        && TRUSTED_PACKAGE_FAMILY_NAME
            == format!(
                "{}_{}",
                TRUSTED_PACKAGE_IDENTITY_NAME, TRUSTED_PACKAGE_PUBLISHER_ID
            )
        && TRUSTED_PACKAGE_ARCHITECTURE == "x64"
        && TRUSTED_APPLICATION_ID == "App"
        && TRUSTED_APPLICATION_EXECUTABLE == "ClashSharp.exe"
        && TRUSTED_APPLICATION_ENTRY_POINT == "Windows.FullTrustApplication"
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

fn absolute_clean_path(path: &Path) -> Result<PathBuf, String> {
    if path.components().any(|component| {
        matches!(
            component,
            std::path::Component::CurDir | std::path::Component::ParentDir
        )
    }) {
        return Err(String::from("installer.trust.payload_path_invalid"));
    }
    if path.is_absolute() {
        Ok(path.to_path_buf())
    } else {
        std::env::current_dir()
            .map(|directory| directory.join(path))
            .map_err(|_| String::from("installer.trust.payload_path_invalid"))
    }
}

fn lock_ordinary_directory_ancestors(path: &Path) -> Result<Vec<File>, String> {
    let mut ancestors = path.ancestors().collect::<Vec<_>>();
    ancestors.reverse();
    let mut guards = Vec::with_capacity(ancestors.len());
    for ancestor in ancestors {
        let metadata = fs::symlink_metadata(ancestor)
            .map_err(|_| String::from("installer.trust.payload_ancestor_unavailable"))?;
        if !metadata.is_dir() || metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.payload_ancestor_unsafe"));
        }
        let guard = open_directory_read_locked(ancestor)
            .map_err(|_| String::from("installer.trust.payload_ancestor_lock_failed"))?;
        let locked_metadata = guard
            .metadata()
            .map_err(|_| String::from("installer.trust.payload_ancestor_lock_failed"))?;
        if !locked_metadata.is_dir() || metadata_is_reparse_point(&locked_metadata) {
            return Err(String::from("installer.trust.payload_ancestor_changed"));
        }
        guards.push(guard);
    }
    Ok(guards)
}

fn enumerate_and_lock_payload_files(
    payload_root: &Path,
    directory_guards: &mut Vec<File>,
) -> Result<(BTreeMap<String, LockedFile>, BTreeSet<String>), String> {
    let mut files = BTreeMap::new();
    let mut directories = BTreeSet::new();
    enumerate_and_lock_payload_directory(
        payload_root,
        payload_root,
        &mut files,
        &mut directories,
        directory_guards,
    )?;
    Ok((files, directories))
}

fn enumerate_and_lock_payload_directory(
    payload_root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, LockedFile>,
    directories: &mut BTreeSet<String>,
    directory_guards: &mut Vec<File>,
) -> Result<(), String> {
    let mut entries = fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.payload_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.payload_reparse_rejected"));
        }
        if metadata.is_dir() {
            let relative = normalize_relative_path(
                path.strip_prefix(payload_root)
                    .map_err(|_| String::from("installer.trust.payload_path_escaped"))?,
            )?;
            if !directories.insert(relative) {
                return Err(String::from("installer.trust.payload_path_collision"));
            }
            let guard = open_directory_read_locked(&path)
                .map_err(|_| String::from("installer.trust.payload_directory_lock_failed"))?;
            let locked_metadata = guard
                .metadata()
                .map_err(|_| String::from("installer.trust.payload_directory_lock_failed"))?;
            if !locked_metadata.is_dir() || metadata_is_reparse_point(&locked_metadata) {
                return Err(String::from("installer.trust.payload_directory_changed"));
            }
            directory_guards.push(guard);
            enumerate_and_lock_payload_directory(
                payload_root,
                &path,
                files,
                directories,
                directory_guards,
            )?;
            continue;
        }
        if !metadata.is_file() {
            return Err(String::from("installer.trust.payload_path_kind_invalid"));
        }
        let handle = open_file_read_locked(&path)
            .map_err(|_| String::from("installer.trust.payload_file_lock_failed"))?;
        let locked_metadata = handle
            .metadata()
            .map_err(|_| String::from("installer.trust.payload_file_lock_failed"))?;
        if !locked_metadata.is_file() || metadata_is_reparse_point(&locked_metadata) {
            return Err(String::from("installer.trust.payload_file_changed"));
        }
        let relative = normalize_relative_path(
            path.strip_prefix(payload_root)
                .map_err(|_| String::from("installer.trust.payload_path_escaped"))?,
        )?;
        let identity = file_identity(&handle)?;
        if files
            .insert(
                relative,
                LockedFile {
                    path,
                    handle,
                    identity,
                    length: locked_metadata.len(),
                },
            )
            .is_some()
        {
            return Err(String::from("installer.trust.payload_path_collision"));
        }
    }
    Ok(())
}

fn enumerate_payload_paths(
    payload_root: &Path,
) -> Result<(BTreeMap<String, PathBuf>, BTreeSet<String>), String> {
    let mut files = BTreeMap::new();
    let mut directories = BTreeSet::new();
    enumerate_payload_path_directory(payload_root, payload_root, &mut files, &mut directories)?;
    Ok((files, directories))
}

fn enumerate_payload_path_directory(
    payload_root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, PathBuf>,
    directories: &mut BTreeSet<String>,
) -> Result<(), String> {
    let mut entries = fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| String::from("installer.trust.payload_directory_unreadable"))?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.payload_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.payload_reparse_rejected"));
        }
        if metadata.is_dir() {
            let relative = normalize_relative_path(
                path.strip_prefix(payload_root)
                    .map_err(|_| String::from("installer.trust.payload_path_escaped"))?,
            )?;
            if !directories.insert(relative) {
                return Err(String::from("installer.trust.payload_path_collision"));
            }
            enumerate_payload_path_directory(payload_root, &path, files, directories)?;
            continue;
        }
        if !metadata.is_file() {
            return Err(String::from("installer.trust.payload_path_kind_invalid"));
        }
        let relative = normalize_relative_path(
            path.strip_prefix(payload_root)
                .map_err(|_| String::from("installer.trust.payload_path_escaped"))?,
        )?;
        if files.insert(relative, path).is_some() {
            return Err(String::from("installer.trust.payload_path_collision"));
        }
    }
    Ok(())
}

fn enumerate_and_lock_registered_package_files(
    install_root: &Path,
    directory_guards: &mut Vec<File>,
) -> Result<(BTreeMap<String, LockedFile>, BTreeSet<String>), String> {
    let root_metadata = fs::symlink_metadata(install_root)
        .map_err(|_| String::from("installer.trust.install_root_unavailable"))?;
    if !root_metadata.is_dir() || metadata_is_reparse_point(&root_metadata) {
        return Err(String::from("installer.trust.install_root_unsafe"));
    }
    let root_guard = open_directory_read_locked(install_root)
        .map_err(|_| String::from("installer.trust.install_root_lock_failed"))?;
    let locked_root_metadata = root_guard
        .metadata()
        .map_err(|_| String::from("installer.trust.install_root_lock_failed"))?;
    if !locked_root_metadata.is_dir() || metadata_is_reparse_point(&locked_root_metadata) {
        return Err(String::from("installer.trust.install_root_changed"));
    }
    directory_guards.push(root_guard);

    let mut files = BTreeMap::new();
    let mut directories = BTreeSet::new();
    enumerate_and_lock_registered_package_directory(
        install_root,
        install_root,
        &mut files,
        &mut directories,
        directory_guards,
    )?;
    Ok((files, directories))
}

fn enumerate_and_lock_registered_package_directory(
    install_root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, LockedFile>,
    directories: &mut BTreeSet<String>,
    directory_guards: &mut Vec<File>,
) -> Result<(), String> {
    let mut entries = fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.package_directory_unreadable"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| String::from("installer.trust.package_directory_unreadable"))?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.package_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.package_reparse_rejected"));
        }
        if metadata.is_dir() {
            let relative = normalize_relative_path(
                path.strip_prefix(install_root)
                    .map_err(|_| String::from("installer.trust.package_path_escaped"))?,
            )?;
            if !directories.insert(relative) {
                return Err(String::from("installer.trust.package_path_collision"));
            }
            let guard = open_directory_read_locked(&path)
                .map_err(|_| String::from("installer.trust.package_directory_lock_failed"))?;
            let locked_metadata = guard
                .metadata()
                .map_err(|_| String::from("installer.trust.package_directory_lock_failed"))?;
            if !locked_metadata.is_dir() || metadata_is_reparse_point(&locked_metadata) {
                return Err(String::from("installer.trust.package_directory_changed"));
            }
            directory_guards.push(guard);
            enumerate_and_lock_registered_package_directory(
                install_root,
                &path,
                files,
                directories,
                directory_guards,
            )?;
            continue;
        }
        if !metadata.is_file() {
            return Err(String::from("installer.trust.package_path_kind_invalid"));
        }
        let handle = open_file_read_locked(&path)
            .map_err(|_| String::from("installer.trust.package_file_lock_failed"))?;
        let locked_metadata = handle
            .metadata()
            .map_err(|_| String::from("installer.trust.package_file_lock_failed"))?;
        if !locked_metadata.is_file() || metadata_is_reparse_point(&locked_metadata) {
            return Err(String::from("installer.trust.package_file_changed"));
        }
        let relative = normalize_relative_path(
            path.strip_prefix(install_root)
                .map_err(|_| String::from("installer.trust.package_path_escaped"))?,
        )?;
        let identity = file_identity(&handle)?;
        if files
            .insert(
                relative,
                LockedFile {
                    path,
                    handle,
                    identity,
                    length: locked_metadata.len(),
                },
            )
            .is_some()
        {
            return Err(String::from("installer.trust.package_path_collision"));
        }
        if files.len() > MAX_REGISTERED_PACKAGE_FILES {
            return Err(String::from("installer.trust.package_file_budget_exceeded"));
        }
    }
    Ok(())
}

fn enumerate_registered_package_paths(
    install_root: &Path,
) -> Result<(BTreeMap<String, PathBuf>, BTreeSet<String>), String> {
    let root_metadata = fs::symlink_metadata(install_root)
        .map_err(|_| String::from("installer.trust.install_root_unavailable"))?;
    if !root_metadata.is_dir() || metadata_is_reparse_point(&root_metadata) {
        return Err(String::from("installer.trust.install_root_unsafe"));
    }
    let mut files = BTreeMap::new();
    let mut directories = BTreeSet::new();
    enumerate_registered_package_path_directory(
        install_root,
        install_root,
        &mut files,
        &mut directories,
    )?;
    Ok((files, directories))
}

fn enumerate_registered_package_path_directory(
    install_root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, PathBuf>,
    directories: &mut BTreeSet<String>,
) -> Result<(), String> {
    let mut entries = fs::read_dir(directory)
        .map_err(|_| String::from("installer.trust.package_directory_unreadable"))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| String::from("installer.trust.package_directory_unreadable"))?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)
            .map_err(|_| String::from("installer.trust.package_file_unreadable"))?;
        if metadata_is_reparse_point(&metadata) {
            return Err(String::from("installer.trust.package_reparse_rejected"));
        }
        let relative = normalize_relative_path(
            path.strip_prefix(install_root)
                .map_err(|_| String::from("installer.trust.package_path_escaped"))?,
        )?;
        if metadata.is_dir() {
            if !directories.insert(relative) {
                return Err(String::from("installer.trust.package_path_collision"));
            }
            enumerate_registered_package_path_directory(install_root, &path, files, directories)?;
        } else if metadata.is_file() {
            if files.insert(relative, path).is_some() {
                return Err(String::from("installer.trust.package_path_collision"));
            }
            if files.len() > MAX_REGISTERED_PACKAGE_FILES {
                return Err(String::from("installer.trust.package_file_budget_exceeded"));
            }
        } else {
            return Err(String::from("installer.trust.package_path_kind_invalid"));
        }
    }
    Ok(())
}

fn owned_file_manifest(
    trusted_files: &[(&str, u64, &str)],
) -> Result<BTreeMap<String, (u64, String)>, String> {
    let mut result = BTreeMap::new();
    for (path, length, hash) in trusted_files {
        let normalized = normalize_relative_path(Path::new(path))?;
        if normalized != *path
            || *length == 0
            || !is_canonical_sha256(hash)
            || DEPLOYED_FOOTPRINT_FILES.contains(path)
            || path.starts_with(&format!("{SYSTEM_PACKAGE_METADATA_ROOT}/"))
        {
            return Err(String::from("installer.trust.anchor_invalid"));
        }
        if result
            .insert(normalized, (*length, (*hash).to_owned()))
            .is_some()
        {
            return Err(String::from("installer.trust.anchor_invalid"));
        }
    }
    if result.is_empty() {
        return Err(String::from("installer.trust.anchor_invalid"));
    }
    Ok(result)
}

fn validate_registered_package_shape(
    actual_files: &BTreeMap<String, LockedFile>,
    actual_directories: &BTreeSet<String>,
    expected_files: &BTreeMap<String, (u64, String)>,
) -> Result<(), String> {
    if actual_files.len() > MAX_REGISTERED_PACKAGE_FILES
        || expected_files
            .keys()
            .any(|path| !actual_files.contains_key(path))
        || DEPLOYED_FOOTPRINT_FILES
            .iter()
            .any(|path| !actual_files.contains_key(*path))
    {
        return Err(String::from("installer.trust.package_file_set_invalid"));
    }

    for (path, file) in actual_files {
        if expected_files.contains_key(path) {
            continue;
        }
        if DEPLOYED_FOOTPRINT_FILES.contains(&path.as_str())
            || is_allowed_system_package_metadata_file(path)
        {
            if file.length == 0 || file.length > MAX_DEPLOYMENT_METADATA_FILE_BYTES {
                return Err(String::from(
                    "installer.trust.package_metadata_file_invalid",
                ));
            }
            continue;
        }
        return Err(format!("installer.trust.package_file_unexpected: {path}"));
    }

    let required_directories =
        expected_parent_directories(expected_files.keys().map(String::as_str));
    if required_directories
        .iter()
        .any(|path| !actual_directories.contains(path))
        || !actual_directories.contains("appxmetadata")
        || actual_directories.iter().any(|path| {
            !required_directories.contains(path)
                && !matches!(
                    path.as_str(),
                    "appxmetadata"
                        | "microsoft.system.package.metadata"
                        | "microsoft.system.package.metadata/autogen"
                )
        })
    {
        return Err(String::from(
            "installer.trust.package_directory_set_invalid",
        ));
    }
    Ok(())
}

fn expected_parent_directories<'a>(paths: impl Iterator<Item = &'a str>) -> BTreeSet<String> {
    let mut result = BTreeSet::new();
    for path in paths {
        let segments = path.split('/').collect::<Vec<_>>();
        for count in 1..segments.len() {
            result.insert(segments[..count].join("/"));
        }
    }
    result
}

fn is_allowed_system_package_metadata_file(path: &str) -> bool {
    if matches!(
        path,
        "microsoft.system.package.metadata/autogen/jsbytecodecache_32"
            | "microsoft.system.package.metadata/autogen/jsbytecodecache_64"
    ) {
        return true;
    }
    if let Some(hash) = path
        .strip_prefix("microsoft.system.package.metadata/resources.")
        .and_then(|value| value.strip_suffix(".pri"))
    {
        return hash.len() == 8 && hash.bytes().all(|value| value.is_ascii_hexdigit());
    }
    let Some(resource) = path
        .strip_prefix("microsoft.system.package.metadata/s-1-")
        .and_then(|value| value.strip_suffix(".pri"))
    else {
        return false;
    };
    let Some((sid_tail, generation)) = resource.rsplit_once("-mergedresources-") else {
        return false;
    };
    !generation.is_empty()
        && generation.bytes().all(|value| value.is_ascii_digit())
        && sid_tail.split('-').count() >= 2
        && sid_tail.split('-').all(|component| {
            !component.is_empty() && component.bytes().all(|value| value.is_ascii_digit())
        })
}

#[cfg(windows)]
fn open_file_read_locked(path: &Path) -> std::io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;

    const FILE_SHARE_READ: u32 = 0x0000_0001;
    const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
    OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
}

#[cfg(not(windows))]
fn open_file_read_locked(path: &Path) -> std::io::Result<File> {
    OpenOptions::new().read(true).open(path)
}

#[cfg(windows)]
fn open_directory_read_locked(path: &Path) -> std::io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;

    const FILE_SHARE_READ: u32 = 0x0000_0001;
    const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;
    const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
    OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
}

#[cfg(not(windows))]
fn open_directory_read_locked(path: &Path) -> std::io::Result<File> {
    OpenOptions::new().read(true).open(path)
}

#[cfg(windows)]
fn file_identity(file: &File) -> Result<FileIdentity, String> {
    use std::os::windows::io::AsRawHandle;

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct FileTime {
        low_date_time: u32,
        high_date_time: u32,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct ByHandleFileInformation {
        file_attributes: u32,
        creation_time: FileTime,
        last_access_time: FileTime,
        last_write_time: FileTime,
        volume_serial_number: u32,
        file_size_high: u32,
        file_size_low: u32,
        number_of_links: u32,
        file_index_high: u32,
        file_index_low: u32,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetFileInformationByHandle(
            file: *mut std::ffi::c_void,
            information: *mut ByHandleFileInformation,
        ) -> i32;
    }

    let mut information = std::mem::MaybeUninit::<ByHandleFileInformation>::uninit();
    // SAFETY: file owns a live Windows handle and information points to writable storage with
    // the documented BY_HANDLE_FILE_INFORMATION layout for the duration of the call.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle().cast(), information.as_mut_ptr()) }
        == 0
    {
        return Err(String::from("installer.trust.file_identity_unavailable"));
    }
    // SAFETY: a successful GetFileInformationByHandle initializes the whole structure.
    let information = unsafe { information.assume_init() };
    Ok(FileIdentity {
        volume: u64::from(information.volume_serial_number),
        index: (u64::from(information.file_index_high) << 32)
            | u64::from(information.file_index_low),
    })
}

#[cfg(unix)]
fn file_identity(file: &File) -> Result<FileIdentity, String> {
    use std::os::unix::fs::MetadataExt;

    let metadata = file
        .metadata()
        .map_err(|_| String::from("installer.trust.file_identity_unavailable"))?;
    Ok(FileIdentity {
        volume: metadata.dev(),
        index: metadata.ino(),
    })
}

fn verify_locked_path_identity(
    current_path: &Path,
    locked: &LockedFile,
    failure_code: &str,
) -> Result<(), String> {
    if current_path != locked.path {
        return Err(failure_code.to_owned());
    }
    let metadata = fs::symlink_metadata(current_path).map_err(|_| failure_code.to_owned())?;
    if !metadata.is_file()
        || metadata_is_reparse_point(&metadata)
        || metadata.len() != locked.length
    {
        return Err(failure_code.to_owned());
    }
    let probe = open_file_read_locked(current_path).map_err(|_| failure_code.to_owned())?;
    if file_identity(&probe)? != locked.identity {
        return Err(failure_code.to_owned());
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

fn verify_open_file(
    file: &mut File,
    expected_hash: &str,
    expected_length: Option<u64>,
    failure_code: &str,
) -> Result<(), String> {
    if !is_canonical_sha256(expected_hash) {
        return Err(String::from("installer.trust.anchor_invalid"));
    }
    let metadata = file.metadata().map_err(|_| failure_code.to_owned())?;
    if !metadata.is_file()
        || metadata_is_reparse_point(&metadata)
        || expected_length.is_some_and(|length| metadata.len() != length)
    {
        return Err(failure_code.to_owned());
    }
    file.seek(SeekFrom::Start(0))
        .map_err(|_| failure_code.to_owned())?;
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
    file.seek(SeekFrom::Start(0))
        .map_err(|_| failure_code.to_owned())?;
    Ok(())
}

fn is_canonical_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
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
        assert!(package_identity_is_canonical());
        if TRUST_ANCHOR_AVAILABLE {
            assert_eq!(TRUSTED_MSIX_SHA256.len(), 64);
            assert!(is_canonical_package_version(TRUSTED_PACKAGE_VERSION));
            assert_eq!(trusted_package_version().unwrap(), TRUSTED_PACKAGE_VERSION);
            assert_eq!(
                trusted_package_identity().unwrap(),
                TrustedPackageIdentity {
                    name: TRUSTED_PACKAGE_IDENTITY_NAME,
                    publisher: TRUSTED_PACKAGE_PUBLISHER,
                    publisher_id: TRUSTED_PACKAGE_PUBLISHER_ID,
                    family_name: TRUSTED_PACKAGE_FAMILY_NAME,
                    version: TRUSTED_PACKAGE_VERSION,
                    architecture: TRUSTED_PACKAGE_ARCHITECTURE,
                    application_id: TRUSTED_APPLICATION_ID,
                    application_executable: TRUSTED_APPLICATION_EXECUTABLE,
                    application_entry_point: TRUSTED_APPLICATION_ENTRY_POINT,
                }
            );
            assert_eq!(TRUSTED_CERTIFICATE_SHA256.len(), 64);
            assert!(!TRUSTED_PAYLOAD_FILES.is_empty());
            assert!(!TRUSTED_ARCHIVE_FILES.is_empty());
            assert!(!TRUSTED_REGISTERED_PACKAGE_FILES.is_empty());
            assert!(!TRUSTED_MACHINE_FILES.is_empty());
        } else {
            assert!(verify_installer_payload(Path::new("missing")).is_err());
            assert!(trusted_package_version().is_err());
            assert!(trusted_package_identity().is_err());
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

    #[cfg(windows)]
    #[test]
    fn locked_payload_handles_block_concurrent_write_replace_and_ancestor_rename() {
        use std::sync::atomic::{AtomicU64, Ordering};

        static SEQUENCE: AtomicU64 = AtomicU64::new(0);
        let sequence = SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir().join(format!(
            "clashsharp-payload-lock-{}-{sequence}",
            std::process::id()
        ));
        let moved_root = root.with_extension("moved");
        fs::create_dir(&root).unwrap();
        let payload = root.join("ClashSharp.msix");
        let moved_payload = root.join("ClashSharp.moved");
        let certificate = root.join("ClashSharp.cer");
        let moved_certificate = root.join("ClashSharp.cer.moved");
        fs::write(&payload, b"anchored bytes").unwrap();
        fs::write(&certificate, b"certificate bytes").unwrap();

        let mut locked = open_file_read_locked(&payload).unwrap();
        let locked_certificate = open_file_read_locked(&certificate).unwrap();
        let expected_hash = lower_hex(&Sha256::digest(b"anchored bytes"));
        verify_open_file(
            &mut locked,
            &expected_hash,
            Some(14),
            "installer.trust.test_failed",
        )
        .unwrap();
        let reader_path = payload.clone();
        let reader_saw_anchored_bytes = std::thread::spawn(move || {
            fs::read(reader_path).is_ok_and(|bytes| bytes == b"anchored bytes")
        })
        .join()
        .unwrap();
        assert!(reader_saw_anchored_bytes);
        let writer_path = payload.clone();
        let write_was_blocked =
            std::thread::spawn(move || OpenOptions::new().write(true).open(writer_path).is_err())
                .join()
                .unwrap();
        assert!(write_was_blocked);
        let certificate_writer_path = certificate.clone();
        let certificate_write_was_blocked = std::thread::spawn(move || {
            OpenOptions::new()
                .write(true)
                .open(certificate_writer_path)
                .is_err()
        })
        .join()
        .unwrap();
        assert!(certificate_write_was_blocked);
        assert!(fs::rename(&payload, &moved_payload).is_err());
        assert!(fs::rename(&certificate, &moved_certificate).is_err());
        drop(locked);
        drop(locked_certificate);
        fs::rename(&payload, &moved_payload).unwrap();
        fs::rename(&moved_payload, &payload).unwrap();
        fs::rename(&certificate, &moved_certificate).unwrap();
        fs::rename(&moved_certificate, &certificate).unwrap();

        let directory_guards = lock_ordinary_directory_ancestors(&root).unwrap();
        assert!(fs::rename(&root, &moved_root).is_err());
        drop(directory_guards);
        fs::rename(&root, &moved_root).unwrap();
        fs::remove_dir_all(&moved_root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn payload_ancestor_lock_rejects_directory_reparse_points() {
        use std::os::windows::fs::symlink_dir;
        use std::sync::atomic::{AtomicU64, Ordering};

        static SEQUENCE: AtomicU64 = AtomicU64::new(0);
        let sequence = SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir().join(format!(
            "clashsharp-payload-reparse-{}-{sequence}",
            std::process::id()
        ));
        let real = root.join("real");
        let link = root.join("link");
        fs::create_dir_all(&real).unwrap();
        symlink_dir(&real, &link).unwrap();
        assert!(lock_ordinary_directory_ancestors(&link).is_err());
        fs::remove_dir(&link).unwrap();
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn registered_package_manifest_rejects_extra_missing_or_changed_content() {
        use std::sync::atomic::{AtomicU64, Ordering};

        static SEQUENCE: AtomicU64 = AtomicU64::new(0);
        let sequence = SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir().join(format!(
            "clashsharp-registered-manifest-{}-{sequence}",
            std::process::id()
        ));
        let service_root = root.join("Binaries").join("Service");
        let appx_metadata_root = root.join("AppxMetadata");
        let system_metadata_root = root
            .join("microsoft.system.package.metadata")
            .join("Autogen");
        fs::create_dir_all(&service_root).unwrap();
        fs::create_dir_all(&appx_metadata_root).unwrap();
        fs::create_dir_all(&system_metadata_root).unwrap();
        fs::write(root.join("ClashSharp.exe"), b"main").unwrap();
        fs::write(service_root.join("Service.dll"), b"service").unwrap();
        fs::write(root.join("AppxBlockMap.xml"), b"deployment block map").unwrap();
        fs::write(root.join("AppxSignature.p7x"), b"deployment signature").unwrap();
        fs::write(
            appx_metadata_root.join("CodeIntegrity.cat"),
            b"deployment CI",
        )
        .unwrap();
        fs::write(system_metadata_root.join("JSByteCodeCache_64"), b"cache").unwrap();
        let main_hash = lower_hex(&Sha256::digest(b"main"));
        let service_hash = lower_hex(&Sha256::digest(b"service"));
        let manifest = [
            ("binaries/service/service.dll", 7, service_hash.as_str()),
            ("clashsharp.exe", 4, main_hash.as_str()),
        ];

        let mut verified = verify_registered_package_file_manifest(&root, &manifest).unwrap();
        verified.reverify().unwrap();
        #[cfg(windows)]
        assert!(
            OpenOptions::new()
                .write(true)
                .open(root.join("ClashSharp.exe"))
                .is_err()
        );
        drop(verified);

        fs::write(root.join("extra.dll"), b"extra").unwrap();
        assert!(verify_registered_package_file_manifest(&root, &manifest).is_err());
        fs::remove_file(root.join("extra.dll")).unwrap();
        fs::write(root.join("ClashSharp.exe"), b"evil").unwrap();
        assert!(verify_registered_package_file_manifest(&root, &manifest).is_err());
        fs::write(root.join("ClashSharp.exe"), b"main").unwrap();
        fs::remove_file(service_root.join("Service.dll")).unwrap();
        assert!(verify_registered_package_file_manifest(&root, &manifest).is_err());
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn windows_generated_package_metadata_allowlist_is_narrow() {
        for valid in [
            "microsoft.system.package.metadata/autogen/jsbytecodecache_32",
            "microsoft.system.package.metadata/autogen/jsbytecodecache_64",
            "microsoft.system.package.metadata/resources.175cb530.pri",
            "microsoft.system.package.metadata/s-1-5-21-1-2-3-1001-mergedresources-2.pri",
        ] {
            assert!(is_allowed_system_package_metadata_file(valid), "{valid}");
        }
        for invalid in [
            "microsoft.system.package.metadata/application.local/evil.dll",
            "microsoft.system.package.metadata/resources.not-hex.pri",
            "microsoft.system.package.metadata/custom.data",
            "arbitrary/extra.dll",
        ] {
            assert!(
                !is_allowed_system_package_metadata_file(invalid),
                "{invalid}"
            );
        }
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
