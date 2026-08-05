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

#[cfg(not(test))]
fn main() {
    println!("cargo:rerun-if-changed=payload");
    println!("cargo:rerun-if-changed=ui/main.slint");
    println!("cargo:rerun-if-changed=LogoInstaller.ico");
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
            fs::write(&output, unavailable_anchor()).expect("write unavailable trust anchor");
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
    let package_version = extract_trusted_package_version(&mut archive)?;
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
    writeln!(
        source,
        "pub const TRUSTED_PACKAGE_VERSION: &str = \"{package_version}\";"
    )
    .unwrap();
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

fn extract_trusted_package_version(archive: &mut ZipArchive<File>) -> Result<String, String> {
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
    parse_appx_identity_version(manifest)
}

fn parse_appx_identity_version(manifest: &str) -> Result<String, String> {
    let mut cursor = 0;
    let mut package_version = None;
    while let Some(relative_start) = manifest[cursor..].find('<') {
        let start = cursor + relative_start;
        if manifest[start..].starts_with("<!--") {
            let relative_end = manifest[start + 4..]
                .find("-->")
                .ok_or_else(|| String::from("AppxManifest.xml comment is unterminated"))?;
            cursor = start + 4 + relative_end + 3;
            continue;
        }
        if manifest[start..].starts_with("<?") {
            let relative_end = manifest[start + 2..]
                .find("?>")
                .ok_or_else(|| String::from("AppxManifest.xml declaration is unterminated"))?;
            cursor = start + 2 + relative_end + 2;
            continue;
        }
        if manifest[start..].starts_with("<!") {
            return Err(String::from(
                "AppxManifest.xml contains unsupported declarations",
            ));
        }

        let end = find_xml_tag_end(manifest, start + 1)?;
        let tag = manifest[start + 1..end].trim();
        cursor = end + 1;
        if tag.is_empty() || tag.starts_with('/') {
            continue;
        }
        let name_end = tag
            .find(|value: char| value.is_ascii_whitespace() || value == '/')
            .unwrap_or(tag.len());
        if &tag[..name_end] != "Identity" {
            continue;
        }
        if package_version.is_some() {
            return Err(String::from(
                "AppxManifest.xml contains duplicate Identity elements",
            ));
        }
        package_version = Some(parse_identity_version_attribute(&tag[name_end..])?);
    }

    package_version.ok_or_else(|| String::from("AppxManifest.xml Identity version is missing"))
}

fn find_xml_tag_end(manifest: &str, start: usize) -> Result<usize, String> {
    let mut quote = None;
    for (offset, value) in manifest[start..].char_indices() {
        match quote {
            Some(expected) if value == expected => quote = None,
            Some(_) => {}
            None if matches!(value, '\'' | '"') => quote = Some(value),
            None if value == '>' => return Ok(start + offset),
            None => {}
        }
    }
    Err(String::from("AppxManifest.xml tag is unterminated"))
}

fn parse_identity_version_attribute(attributes: &str) -> Result<String, String> {
    let bytes = attributes.trim_end_matches('/').as_bytes();
    let mut cursor = 0;
    let mut version = None;
    while cursor < bytes.len() {
        while cursor < bytes.len() && bytes[cursor].is_ascii_whitespace() {
            cursor += 1;
        }
        if cursor == bytes.len() {
            break;
        }
        let name_start = cursor;
        while cursor < bytes.len()
            && (bytes[cursor].is_ascii_alphanumeric()
                || matches!(bytes[cursor], b':' | b'_' | b'-' | b'.'))
        {
            cursor += 1;
        }
        if name_start == cursor {
            return Err(String::from(
                "AppxManifest.xml Identity attribute is invalid",
            ));
        }
        let name = &attributes[name_start..cursor];
        while cursor < bytes.len() && bytes[cursor].is_ascii_whitespace() {
            cursor += 1;
        }
        if bytes.get(cursor) != Some(&b'=') {
            return Err(String::from(
                "AppxManifest.xml Identity attribute is invalid",
            ));
        }
        cursor += 1;
        while cursor < bytes.len() && bytes[cursor].is_ascii_whitespace() {
            cursor += 1;
        }
        let Some(delimiter @ (b'\'' | b'"')) = bytes.get(cursor).copied() else {
            return Err(String::from(
                "AppxManifest.xml Identity attribute is unquoted",
            ));
        };
        cursor += 1;
        let value_start = cursor;
        while cursor < bytes.len() && bytes[cursor] != delimiter {
            cursor += 1;
        }
        if cursor == bytes.len() {
            return Err(String::from(
                "AppxManifest.xml Identity attribute is unterminated",
            ));
        }
        let value = &attributes[value_start..cursor];
        cursor += 1;
        if name == "Version" && version.replace(value.to_owned()).is_some() {
            return Err(String::from(
                "AppxManifest.xml Identity version is duplicated",
            ));
        }
    }

    let version =
        version.ok_or_else(|| String::from("AppxManifest.xml Identity version is missing"))?;
    if !is_canonical_package_version(&version) {
        return Err(String::from(
            "AppxManifest.xml Identity version is noncanonical",
        ));
    }
    Ok(version)
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

fn unavailable_anchor() -> &'static str {
    "pub const TRUST_ANCHOR_AVAILABLE: bool = false;\n\
     pub const TRUSTED_MSIX_SHA256: &str = \"\";\n\
     pub const TRUSTED_PACKAGE_VERSION: &str = \"\";\n\
     pub const TRUSTED_CERTIFICATE_SHA256: &str = \"\";\n\
     pub const TRUSTED_PRIMARY_MSIX_RELATIVE_PATH: &str = \"\";\n\
     pub const TRUSTED_CERTIFICATE_RELATIVE_PATH: &str = \"\";\n\
     pub const TRUSTED_PAYLOAD_FILES: &[(&str, u64, &str)] = &[];\n\
     pub const TRUSTED_MACHINE_FILES: &[(&str, u64, &str)] = &[];\n"
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

    #[test]
    fn appx_identity_version_parser_ignores_comments_and_requires_one_identity() {
        let manifest = r#"<?xml version="1.0" encoding="utf-8"?>
            <!-- <Identity Version="9.9.9.9" /> -->
            <Package>
              <Identity Name="ClashSharp" Publisher="CN=linzh" Version="1.2.3.4" />
              <mp:PhoneIdentity PhoneProductId="x" />
            </Package>"#;

        assert_eq!(parse_appx_identity_version(manifest).unwrap(), "1.2.3.4");
        assert!(
            parse_appx_identity_version(
                r#"<Package><Identity Version="1.0.0.0"/><Identity Version="2.0.0.0"/></Package>"#
            )
            .is_err()
        );
    }

    #[test]
    fn appx_identity_version_parser_rejects_noncanonical_versions() {
        for version in ["1.2.3", "01.2.3.4", "1.2.3.65536", "1.2.3.four"] {
            let manifest = format!(r#"<Package><Identity Version="{version}"/></Package>"#);
            assert!(
                parse_appx_identity_version(&manifest).is_err(),
                "accepted {version}"
            );
        }
    }
}
