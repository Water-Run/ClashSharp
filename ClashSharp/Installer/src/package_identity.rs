//! Typed CurrentUser package identity and version validation.

use std::fmt;
use std::str::FromStr;

use serde::Deserialize;

use crate::trust_anchor::{
    TRUSTED_PACKAGE_ARCHITECTURE, TRUSTED_PACKAGE_FAMILY_NAME, TRUSTED_PACKAGE_IDENTITY_NAME,
    TRUSTED_PACKAGE_PUBLISHER, TRUSTED_PACKAGE_PUBLISHER_ID,
};

const MAX_REGISTRATION_JSON_BYTES: usize = 16 * 1024;

/// A canonical four-component Windows package version.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct PackageVersion([u16; 4]);

impl PackageVersion {
    /// Parses canonical `major.minor.build.revision` text without accepting aliases.
    pub fn parse(text: &str) -> Result<Self, String> {
        text.parse()
    }
}

impl FromStr for PackageVersion {
    type Err = String;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        let components = text.split('.').collect::<Vec<_>>();
        if components.len() != 4 {
            return Err(String::from("installer.package.version_invalid"));
        }
        let mut parsed = [0_u16; 4];
        for (index, component) in components.into_iter().enumerate() {
            if component.is_empty()
                || !component.bytes().all(|value| value.is_ascii_digit())
                || (component != "0" && component.starts_with('0'))
            {
                return Err(String::from("installer.package.version_invalid"));
            }
            parsed[index] = component
                .parse()
                .map_err(|_| String::from("installer.package.version_invalid"))?;
        }
        Ok(Self(parsed))
    }
}

impl fmt::Display for PackageVersion {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "{}.{}.{}.{}",
            self.0[0], self.0[1], self.0[2], self.0[3]
        )
    }
}

/// Direction of a proposed trusted payload relative to the installed package.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DeploymentVersionChange {
    /// The payload is newer than the registered package.
    Upgrade,
    /// The payload and registered package have the same version.
    Same,
    /// The payload is older than the registered package.
    Downgrade,
}

/// Classifies a proposed deployment without granting any downgrade capability.
#[must_use]
pub fn classify_deployment_version(
    installed: PackageVersion,
    payload: PackageVersion,
) -> DeploymentVersionChange {
    match payload.cmp(&installed) {
        std::cmp::Ordering::Greater => DeploymentVersionChange::Upgrade,
        std::cmp::Ordering::Equal => DeploymentVersionChange::Same,
        std::cmp::Ordering::Less => DeploymentVersionChange::Downgrade,
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RegistrationWire {
    name: String,
    version: String,
    architecture: String,
    resource_id: String,
    package_full_name: String,
    package_family_name: String,
    publisher: String,
    publisher_id: String,
}

/// Strict, identity-validated registration for the current Windows user.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CurrentUserPackageRegistration {
    version: PackageVersion,
    package_full_name: String,
}

impl CurrentUserPackageRegistration {
    /// Parses the bounded JSON object emitted by the fixed CurrentUser package query.
    pub fn parse_json(text: &str) -> Result<Self, String> {
        if text.is_empty() || text.len() > MAX_REGISTRATION_JSON_BYTES {
            return Err(String::from("installer.package.registration_invalid"));
        }
        let wire: RegistrationWire = serde_json::from_str(text)
            .map_err(|_| String::from("installer.package.registration_invalid"))?;
        let version = PackageVersion::parse(&wire.version)
            .map_err(|_| String::from("installer.package.registration_invalid"))?;
        let expected_full_name = format!(
            "{}_{}_{}__{}",
            TRUSTED_PACKAGE_IDENTITY_NAME,
            version,
            TRUSTED_PACKAGE_ARCHITECTURE,
            TRUSTED_PACKAGE_PUBLISHER_ID
        );
        if wire.name != TRUSTED_PACKAGE_IDENTITY_NAME
            || !wire
                .architecture
                .eq_ignore_ascii_case(TRUSTED_PACKAGE_ARCHITECTURE)
            || !wire.resource_id.is_empty()
            || wire.package_full_name != expected_full_name
            || wire.package_family_name != TRUSTED_PACKAGE_FAMILY_NAME
            || wire.publisher != TRUSTED_PACKAGE_PUBLISHER
            || wire.publisher_id != TRUSTED_PACKAGE_PUBLISHER_ID
        {
            return Err(String::from("installer.package.identity_mismatch"));
        }
        Ok(Self {
            version,
            package_full_name: wire.package_full_name,
        })
    }

    /// Gets the installed four-component numeric package version.
    #[must_use]
    pub fn version(&self) -> PackageVersion {
        self.version
    }

    /// Gets the exact package full name validated against all trusted identity fields.
    #[must_use]
    pub fn package_full_name(&self) -> &str {
        &self.package_full_name
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn registration_json(version: &str, family_name: &str) -> String {
        serde_json::json!({
            "name": TRUSTED_PACKAGE_IDENTITY_NAME,
            "version": version,
            "architecture": "X64",
            "resourceId": "",
            "packageFullName": format!(
                "{TRUSTED_PACKAGE_IDENTITY_NAME}_{version}_{TRUSTED_PACKAGE_ARCHITECTURE}__{TRUSTED_PACKAGE_PUBLISHER_ID}"
            ),
            "packageFamilyName": family_name,
            "publisher": TRUSTED_PACKAGE_PUBLISHER,
            "publisherId": TRUSTED_PACKAGE_PUBLISHER_ID,
        })
        .to_string()
    }

    #[test]
    fn package_versions_are_strict_and_numerically_ordered() {
        let installed = PackageVersion::parse("2.10.3.4").unwrap();
        assert_eq!(
            classify_deployment_version(installed, PackageVersion::parse("2.11.0.0").unwrap()),
            DeploymentVersionChange::Upgrade
        );
        assert_eq!(
            classify_deployment_version(installed, PackageVersion::parse("2.10.3.4").unwrap()),
            DeploymentVersionChange::Same
        );
        assert_eq!(
            classify_deployment_version(installed, PackageVersion::parse("2.9.65535.0").unwrap()),
            DeploymentVersionChange::Downgrade
        );
        for invalid in ["", "1.2.3", "01.2.3.4", "1.2.3.65536", "1.2.3.x"] {
            assert!(
                PackageVersion::parse(invalid).is_err(),
                "accepted {invalid}"
            );
        }
    }

    #[test]
    fn current_user_registration_requires_the_generated_identity_and_family() {
        let registration = CurrentUserPackageRegistration::parse_json(&registration_json(
            "1.0.0.0",
            TRUSTED_PACKAGE_FAMILY_NAME,
        ))
        .unwrap();
        assert_eq!(registration.version().to_string(), "1.0.0.0");
        assert!(registration.package_full_name().contains("_1.0.0.0_x64__"));

        assert!(
            CurrentUserPackageRegistration::parse_json(&registration_json(
                "1.0.0.0",
                "ClashSharp_wrongfamily"
            ))
            .is_err()
        );
        assert!(
            CurrentUserPackageRegistration::parse_json(&registration_json(
                "01.0.0.0",
                TRUSTED_PACKAGE_FAMILY_NAME
            ))
            .is_err()
        );
    }
}
