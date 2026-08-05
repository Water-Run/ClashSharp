//! Pure durable-transaction protocol shared by the interactive Installer and its helper.
//!
//! This module deliberately performs no file-system or Windows mutation. It defines the
//! bounded journal document, canonical release identity, and the only legal phase advances;
//! persistence and recovery effects remain responsibilities of the Installer coordinator.

use serde::{Deserialize, Serialize};

use crate::service_plan::{generate_token, validate_owner_sid};

/// Fixed machine-relative journal path below `CommonApplicationData`.
pub const INSTALLER_TRANSACTION_RELATIVE_PATH: &str = r"ClashSharp\Installer\transaction.json";

/// Current strict journal schema.
pub const INSTALLER_TRANSACTION_SCHEMA: u32 = 1;

/// Maximum accepted UTF-8 JSON document size.
pub const MAX_INSTALLER_TRANSACTION_BYTES: usize = 4096;

/// Durable phase of one Installer package-and-machine transaction.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum InstallerTransactionPhase {
    /// Recovery intent and the immutable release identity are durable.
    Prepared,
    /// The target user's exact package deployment has been independently verified.
    PackageCommitted,
    /// Machine payload, SCM configuration, and owner association have committed.
    MachineCommitted,
    /// The package and machine generation have passed final readiness verification.
    Verified,
}

impl InstallerTransactionPhase {
    /// Returns whether `next` is an idempotent replay or the single allowed forward step.
    #[must_use]
    pub fn can_transition_to(self, next: Self) -> bool {
        self == next
            || matches!(
                (self, next),
                (Self::Prepared, Self::PackageCommitted)
                    | (Self::PackageCommitted, Self::MachineCommitted)
                    | (Self::MachineCommitted, Self::Verified)
            )
    }
}

/// Strict, bounded durable journal for one Installer transaction.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InstallerTransactionJournal {
    schema: u32,
    transaction_id: String,
    target_sid: String,
    allow_reassociation: bool,
    expected_package_version: String,
    installer_payload_sha256: String,
    phase: InstallerTransactionPhase,
}

impl InstallerTransactionJournal {
    /// Creates a prepared journal with an explicit canonical transaction identifier.
    pub fn new(
        transaction_id: &str,
        target_sid: &str,
        allow_reassociation: bool,
        expected_package_version: &str,
        installer_payload_sha256: &str,
    ) -> Result<Self, String> {
        let journal = Self {
            schema: INSTALLER_TRANSACTION_SCHEMA,
            transaction_id: transaction_id.to_owned(),
            target_sid: target_sid.to_owned(),
            allow_reassociation,
            expected_package_version: expected_package_version.to_owned(),
            installer_payload_sha256: installer_payload_sha256.to_owned(),
            phase: InstallerTransactionPhase::Prepared,
        };
        journal.validate()?;
        Ok(journal)
    }

    /// Creates a prepared journal with a cryptographically random transaction identifier.
    pub fn create(
        target_sid: &str,
        allow_reassociation: bool,
        expected_package_version: &str,
        installer_payload_sha256: &str,
    ) -> Result<Self, String> {
        Self::new(
            &generate_transaction_id()?,
            target_sid,
            allow_reassociation,
            expected_package_version,
            installer_payload_sha256,
        )
    }

    /// Parses and validates one bounded UTF-8 JSON journal.
    pub fn parse(bytes: &[u8]) -> Result<Self, String> {
        if bytes.is_empty() || bytes.len() > MAX_INSTALLER_TRANSACTION_BYTES {
            return Err(String::from("installer.transaction.size_invalid"));
        }

        let journal: Self = serde_json::from_slice(bytes)
            .map_err(|_| String::from("installer.transaction.json_invalid"))?;
        journal.validate()?;
        Ok(journal)
    }

    /// Serializes the validated journal into its canonical compact JSON representation.
    pub fn to_json(&self) -> Result<String, String> {
        self.validate()?;
        let json = serde_json::to_string(self)
            .map_err(|_| String::from("installer.transaction.serialize_failed"))?;
        if json.len() > MAX_INSTALLER_TRANSACTION_BYTES {
            return Err(String::from("installer.transaction.size_invalid"));
        }
        Ok(json)
    }

    /// Advances the phase exactly once, or accepts an idempotent replay of the current phase.
    pub fn transition_to(&mut self, next: InstallerTransactionPhase) -> Result<(), String> {
        if !self.phase.can_transition_to(next) {
            return Err(String::from(
                "installer.transaction.phase_transition_invalid",
            ));
        }
        self.phase = next;
        Ok(())
    }

    /// Promotes an ordinary Install transaction into an explicit Repair transaction.
    ///
    /// Promotion is accepted only before machine commit. Replaying an already-promoted journal
    /// is idempotent at every phase, while an impossible unpromoted machine/verified phase fails
    /// closed instead of rewriting committed transaction identity.
    pub fn upgrade_to_explicit_repair(&mut self) -> Result<(), String> {
        if self.allow_reassociation {
            return Ok(());
        }
        if !matches!(
            self.phase,
            InstallerTransactionPhase::Prepared | InstallerTransactionPhase::PackageCommitted
        ) {
            return Err(String::from("installer.transaction.repair_upgrade_invalid"));
        }
        self.allow_reassociation = true;
        Ok(())
    }

    /// Returns whether a new Installer invocation is the exact release allowed to resume this journal.
    pub fn matches_same_release(
        &self,
        target_sid: &str,
        allow_reassociation: bool,
        expected_package_version: &str,
        installer_payload_sha256: &str,
    ) -> Result<bool, String> {
        validate_owner_sid(target_sid)
            .map_err(|_| String::from("installer.transaction.target_sid_invalid"))?;
        validate_expected_package_version(expected_package_version)?;
        validate_installer_payload_sha256(installer_payload_sha256)?;

        Ok(self.target_sid == target_sid
            && self.allow_reassociation == allow_reassociation
            && self.expected_package_version == expected_package_version
            && self.installer_payload_sha256 == installer_payload_sha256)
    }

    /// Gets the immutable transaction identifier.
    #[must_use]
    pub fn transaction_id(&self) -> &str {
        &self.transaction_id
    }

    /// Gets the immutable target interactive-user SID.
    #[must_use]
    pub fn target_sid(&self) -> &str {
        &self.target_sid
    }

    /// Gets whether this transaction explicitly permits an owner reassociation.
    #[must_use]
    pub const fn allow_reassociation(&self) -> bool {
        self.allow_reassociation
    }

    /// Gets the exact target package version bound into this transaction.
    #[must_use]
    pub fn expected_package_version(&self) -> &str {
        &self.expected_package_version
    }

    /// Gets the lowercase SHA-256 of the Installer release payload.
    #[must_use]
    pub fn installer_payload_sha256(&self) -> &str {
        &self.installer_payload_sha256
    }

    /// Gets the current durable phase.
    #[must_use]
    pub const fn phase(&self) -> InstallerTransactionPhase {
        self.phase
    }

    fn validate(&self) -> Result<(), String> {
        if self.schema != INSTALLER_TRANSACTION_SCHEMA {
            return Err(String::from("installer.transaction.schema_invalid"));
        }
        validate_transaction_id(&self.transaction_id)?;
        validate_owner_sid(&self.target_sid)
            .map_err(|_| String::from("installer.transaction.target_sid_invalid"))?;
        validate_expected_package_version(&self.expected_package_version)?;
        validate_installer_payload_sha256(&self.installer_payload_sha256)
    }
}

/// Generates a cryptographically random canonical 256-bit transaction identifier.
pub fn generate_transaction_id() -> Result<String, String> {
    generate_token().map_err(|_| String::from("installer.transaction.id_generation_failed"))
}

/// Validates a canonical lowercase 256-bit transaction identifier.
pub fn validate_transaction_id(value: &str) -> Result<(), String> {
    validate_lower_hex_256(value, "installer.transaction.id_invalid")
}

/// Validates a canonical four-component MSIX package version.
pub fn validate_expected_package_version(value: &str) -> Result<(), String> {
    let parts = value.split('.').collect::<Vec<_>>();
    if parts.len() != 4
        || parts.iter().any(|part| {
            part.is_empty()
                || !part.bytes().all(|byte| byte.is_ascii_digit())
                || part.len() > 1 && part.starts_with('0')
                || part.parse::<u16>().is_err()
        })
    {
        return Err(String::from(
            "installer.transaction.package_version_invalid",
        ));
    }
    Ok(())
}

/// Validates a canonical lowercase SHA-256 bound to the Installer release payload.
pub fn validate_installer_payload_sha256(value: &str) -> Result<(), String> {
    validate_lower_hex_256(value, "installer.transaction.payload_hash_invalid")
}

fn validate_lower_hex_256(value: &str, error_code: &str) -> Result<(), String> {
    if value.len() != 64
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(error_code.to_owned());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const TRANSACTION_ID: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const PAYLOAD_HASH: &str = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    const TARGET_SID: &str = "S-1-5-21-100-200-300-1001";
    const VERSION: &str = "1.2.3.4";

    fn journal() -> InstallerTransactionJournal {
        InstallerTransactionJournal::new(TRANSACTION_ID, TARGET_SID, true, VERSION, PAYLOAD_HASH)
            .unwrap()
    }

    #[test]
    fn fixed_path_and_canonical_prepared_json_are_stable() {
        assert_eq!(
            INSTALLER_TRANSACTION_RELATIVE_PATH,
            r"ClashSharp\Installer\transaction.json"
        );
        assert_eq!(
            journal().to_json().unwrap(),
            format!(
                r#"{{"schema":1,"transactionId":"{TRANSACTION_ID}","targetSid":"{TARGET_SID}","allowReassociation":true,"expectedPackageVersion":"{VERSION}","installerPayloadSha256":"{PAYLOAD_HASH}","phase":"prepared"}}"#
            )
        );
    }

    #[test]
    fn codec_round_trips_every_phase() {
        let mut value = journal();
        for phase in [
            InstallerTransactionPhase::Prepared,
            InstallerTransactionPhase::PackageCommitted,
            InstallerTransactionPhase::MachineCommitted,
            InstallerTransactionPhase::Verified,
        ] {
            value.transition_to(phase).unwrap();
            let json = value.to_json().unwrap();
            assert_eq!(
                InstallerTransactionJournal::parse(json.as_bytes()).unwrap(),
                value
            );
        }
    }

    #[test]
    fn codec_rejects_unbounded_or_non_strict_documents() {
        let canonical = journal().to_json().unwrap();
        assert_eq!(
            InstallerTransactionJournal::parse(&[]),
            Err(String::from("installer.transaction.size_invalid"))
        );
        assert_eq!(
            InstallerTransactionJournal::parse(&vec![b'x'; MAX_INSTALLER_TRANSACTION_BYTES + 1]),
            Err(String::from("installer.transaction.size_invalid"))
        );

        for invalid in [
            canonical.replace("\"schema\":1", "\"schema\":2"),
            canonical.replace("\"phase\":\"prepared\"", "\"phase\":\"packageDeployed\""),
            canonical.replace(
                "\"phase\":\"prepared\"",
                "\"phase\":\"prepared\",\"extra\":true",
            ),
            canonical.replacen("\"schema\":1", "\"schema\":1,\"schema\":1", 1),
        ] {
            assert!(
                InstallerTransactionJournal::parse(invalid.as_bytes()).is_err(),
                "accepted invalid journal: {invalid}"
            );
        }
    }

    #[test]
    fn canonical_field_validators_reject_shape_and_case_drift() {
        for invalid_id in [
            "",
            "0123456789abcdef",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeF",
            "g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ] {
            assert!(validate_transaction_id(invalid_id).is_err());
        }
        assert!(validate_transaction_id(TRANSACTION_ID).is_ok());

        for invalid_version in [
            "",
            "1",
            "1.2.3",
            "1.2.3.4.5",
            "01.2.3.4",
            "1.02.3.4",
            "1.2.3.-1",
            "1.2.3.65536",
        ] {
            assert!(
                validate_expected_package_version(invalid_version).is_err(),
                "accepted version {invalid_version}"
            );
        }
        for valid_version in ["0.0.0.0", "1.2.3.4", "65535.65535.65535.65535"] {
            assert!(validate_expected_package_version(valid_version).is_ok());
        }

        assert!(validate_installer_payload_sha256(PAYLOAD_HASH).is_ok());
        assert!(validate_installer_payload_sha256(&PAYLOAD_HASH.to_ascii_uppercase()).is_err());
        assert!(
            InstallerTransactionJournal::new(
                TRANSACTION_ID,
                "S-1-5-18",
                false,
                VERSION,
                PAYLOAD_HASH,
            )
            .is_err()
        );
    }

    #[test]
    fn phase_transition_table_allows_only_replay_or_one_forward_step() {
        use InstallerTransactionPhase::{MachineCommitted, PackageCommitted, Prepared, Verified};
        let phases = [Prepared, PackageCommitted, MachineCommitted, Verified];
        for current in phases {
            for next in phases {
                let expected = current == next
                    || matches!(
                        (current, next),
                        (Prepared, PackageCommitted)
                            | (PackageCommitted, MachineCommitted)
                            | (MachineCommitted, Verified)
                    );
                assert_eq!(
                    current.can_transition_to(next),
                    expected,
                    "unexpected transition result: {current:?} -> {next:?}"
                );
            }
        }
    }

    #[test]
    fn invalid_transition_does_not_mutate_the_journal() {
        let mut value = journal();
        let before = value.clone();
        assert_eq!(
            value.transition_to(InstallerTransactionPhase::MachineCommitted),
            Err(String::from(
                "installer.transaction.phase_transition_invalid"
            ))
        );
        assert_eq!(value, before);

        value
            .transition_to(InstallerTransactionPhase::PackageCommitted)
            .unwrap();
        value
            .transition_to(InstallerTransactionPhase::PackageCommitted)
            .unwrap();
        assert_eq!(value.phase(), InstallerTransactionPhase::PackageCommitted);
    }

    #[test]
    fn same_release_resume_requires_every_immutable_release_field() {
        let value = journal();
        assert!(
            value
                .matches_same_release(TARGET_SID, true, VERSION, PAYLOAD_HASH)
                .unwrap()
        );

        let cases = [
            ("S-1-5-21-100-200-300-1002", true, VERSION, PAYLOAD_HASH),
            (TARGET_SID, false, VERSION, PAYLOAD_HASH),
            (TARGET_SID, true, "1.2.3.5", PAYLOAD_HASH),
            (TARGET_SID, true, VERSION, TRANSACTION_ID),
        ];
        for (sid, allow_reassociation, version, hash) in cases {
            assert!(
                !value
                    .matches_same_release(sid, allow_reassociation, version, hash)
                    .unwrap()
            );
        }
        assert!(
            value
                .matches_same_release("not-a-sid", true, VERSION, PAYLOAD_HASH)
                .is_err()
        );
        assert!(
            value
                .matches_same_release(TARGET_SID, true, "1.2.3", PAYLOAD_HASH)
                .is_err()
        );
    }

    #[test]
    fn explicit_repair_upgrade_is_bounded_to_pre_machine_phases_and_idempotent() {
        use InstallerTransactionPhase::{MachineCommitted, PackageCommitted, Prepared, Verified};

        for phase in [Prepared, PackageCommitted] {
            let mut value = InstallerTransactionJournal::new(
                TRANSACTION_ID,
                TARGET_SID,
                false,
                VERSION,
                PAYLOAD_HASH,
            )
            .unwrap();
            if phase == PackageCommitted {
                value.transition_to(PackageCommitted).unwrap();
            }

            value.upgrade_to_explicit_repair().unwrap();
            value.upgrade_to_explicit_repair().unwrap();
            assert!(value.allow_reassociation());
            assert_eq!(value.phase(), phase);
            assert!(
                value
                    .matches_same_release(TARGET_SID, true, VERSION, PAYLOAD_HASH)
                    .unwrap()
            );
        }

        for phase in [MachineCommitted, Verified] {
            let mut value = InstallerTransactionJournal::new(
                TRANSACTION_ID,
                TARGET_SID,
                false,
                VERSION,
                PAYLOAD_HASH,
            )
            .unwrap();
            value.transition_to(PackageCommitted).unwrap();
            value.transition_to(MachineCommitted).unwrap();
            if phase == Verified {
                value.transition_to(Verified).unwrap();
            }

            assert_eq!(
                value.upgrade_to_explicit_repair(),
                Err(String::from("installer.transaction.repair_upgrade_invalid"))
            );
            assert!(!value.allow_reassociation());

            let mut already_promoted = value;
            already_promoted.allow_reassociation = true;
            already_promoted.upgrade_to_explicit_repair().unwrap();
            assert!(already_promoted.allow_reassociation());
            assert_eq!(already_promoted.phase(), phase);
        }
    }

    #[test]
    fn create_generates_distinct_canonical_ids_and_starts_prepared() {
        let first =
            InstallerTransactionJournal::create(TARGET_SID, false, VERSION, PAYLOAD_HASH).unwrap();
        let second =
            InstallerTransactionJournal::create(TARGET_SID, false, VERSION, PAYLOAD_HASH).unwrap();

        assert_eq!(first.phase(), InstallerTransactionPhase::Prepared);
        assert!(validate_transaction_id(first.transaction_id()).is_ok());
        assert!(validate_transaction_id(second.transaction_id()).is_ok());
        assert_ne!(first.transaction_id(), second.transaction_id());
        assert_eq!(first.target_sid(), TARGET_SID);
        assert!(!first.allow_reassociation());
        assert_eq!(first.expected_package_version(), VERSION);
        assert_eq!(first.installer_payload_sha256(), PAYLOAD_HASH);
    }
}
