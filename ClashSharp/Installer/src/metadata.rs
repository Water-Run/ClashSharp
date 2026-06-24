use std::path::Path;

/// Maximum number of Unicode scalar values shown for a compacted path.
const MAX_PATH_CHARS: usize = 44;

/// Shortens long filesystem paths while preserving the most useful tail segment.
///
/// Short paths are returned unchanged. Longer paths are prefixed with `...`,
/// keeping the rightmost segment because installer payload paths usually differ
/// most usefully near their file or directory tail.
///
/// # Examples
///
/// ```
/// use std::path::Path;
///
/// let path = Path::new(r"D:\ClashSharp\Installer\payload");
/// assert_eq!(
///     clashsharp_installer::metadata::compact_path(path),
///     r"D:\ClashSharp\Installer\payload"
/// );
/// ```
///
/// ```
/// use std::path::Path;
///
/// let path = Path::new(
///     r"D:\Coding\ClashSharp\ClashSharp\Installer\target\debug\payload",
/// );
/// let compact = clashsharp_installer::metadata::compact_path(path);
///
/// assert!(compact.starts_with("..."));
/// assert!(compact.ends_with(r"Installer\target\debug\payload"));
/// assert!(compact.chars().count() <= 44);
/// ```
#[must_use]
pub fn compact_path(path: &Path) -> String {
    let text = path.to_string_lossy();

    if text.chars().count() <= MAX_PATH_CHARS {
        return text.into_owned();
    }

    let tail = text
        .chars()
        .rev()
        .take(MAX_PATH_CHARS - 3)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect::<String>();
    format!("...{tail}")
}

/// Extracts the version segment from a payload package file name.
///
/// The expected package shape is `ClashSharp_<version>_<platform>.msix` or the
/// same stem with an `.msixbundle` extension. The parser intentionally ignores
/// platform and extension; it only validates that the second underscore-delimited
/// segment looks like a dotted numeric version.
///
/// # Examples
///
/// ```
/// use std::path::Path;
///
/// assert_eq!(
///     clashsharp_installer::metadata::parse_version_from_package_name(
///         Path::new("ClashSharp_1.2.3.4_x64.msix"),
///     ),
///     Some(String::from("1.2.3.4"))
/// );
/// ```
///
/// ```
/// use std::path::Path;
///
/// assert_eq!(
///     clashsharp_installer::metadata::parse_version_from_package_name(
///         Path::new("ClashSharp_x64.msix"),
///     ),
///     None
/// );
/// ```
#[must_use]
pub fn parse_version_from_package_name(path: &Path) -> Option<String> {
    let stem = path.file_stem()?.to_str()?;
    let mut parts = stem.split('_');
    let _name = parts.next()?;
    let version = parts.next()?;

    if version.chars().all(|ch| ch.is_ascii_digit() || ch == '.') && version.contains('.') {
        Some(version.to_owned())
    } else {
        None
    }
}

/// Reads the package identity version from Appx manifest XML text.
///
/// This helper is deliberately small: the installer only needs the `Version`
/// attribute from the first `<Identity ...>` element in a trusted local manifest.
/// Full XML parsing would add dependency weight for no current behavioral gain.
///
/// # Examples
///
/// ```
/// let manifest = r#"
/// <Package>
///   <Identity
///     Name="67dc1dc3-13fd-46c5-84f4-2932d94b566f"
///     Publisher="CN=linzh"
///     Version="2.3.4.5" />
/// </Package>
/// "#;
///
/// assert_eq!(
///     clashsharp_installer::metadata::read_manifest_version_text(manifest),
///     Some(String::from("2.3.4.5"))
/// );
/// ```
#[must_use]
pub fn read_manifest_version_text(text: &str) -> Option<String> {
    let identity_start = text.find("<Identity")?;
    let identity_tail = &text[identity_start..];
    let identity_end = identity_tail.find('>')?;
    let identity = &identity_tail[..identity_end];
    let version_start = identity.find("Version=\"")? + "Version=\"".len();
    let version_tail = &identity[version_start..];
    let version_end = version_tail.find('"')?;
    let version = &version_tail[..version_end];

    if version.is_empty() {
        None
    } else {
        Some(version.to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_version_rejects_non_numeric_segments() {
        let path = Path::new("ClashSharp_preview_x64.msix");

        assert_eq!(parse_version_from_package_name(path), None);
    }

    #[test]
    fn manifest_parser_ignores_missing_identity() {
        assert_eq!(read_manifest_version_text("<Package />"), None);
    }

    #[test]
    fn manifest_parser_ignores_empty_version() {
        let manifest = r#"<Package><Identity Version="" /></Package>"#;

        assert_eq!(read_manifest_version_text(manifest), None);
    }
}
