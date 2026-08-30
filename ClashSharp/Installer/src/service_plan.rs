//! Pure planning and validation for the privileged mihomo service helper.
//!
//! The interactive installer owns CurrentUser certificate and MSIX work.  This
//! module deliberately limits the elevated boundary to a canonical SID, a
//! random token, and fixed action flags; paths are derived from the target
//! user's registered package and well-known machine folders inside the helper.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

/// MSIX identity values generated from the final package (or the source manifest for an
/// intentionally trust-anchor-free development build). No second handwritten identity exists.
pub use crate::trust_anchor::{
    TRUSTED_PACKAGE_ARCHITECTURE as PACKAGE_ARCHITECTURE,
    TRUSTED_PACKAGE_FAMILY_NAME as PACKAGE_FAMILY_NAME,
    TRUSTED_PACKAGE_IDENTITY_NAME as PACKAGE_IDENTITY_NAME,
    TRUSTED_PACKAGE_PUBLISHER as PACKAGE_PUBLISHER,
    TRUSTED_PACKAGE_PUBLISHER_ID as PACKAGE_PUBLISHER_ID,
};

/// Fixed Windows service name shared with the application.
pub const SERVICE_NAME: &str = "ClashSharpMihomo";

/// Fixed Windows service display name.
pub const SERVICE_DISPLAY_NAME: &str = "Clash# Mihomo Service";

/// Stable machine association relative to CommonApplicationData.
pub const ASSOCIATION_RELATIVE_PATH: &str = r"ClashSharp\MihomoService\association.json";

/// Stable machine payload relative to Program Files.
pub const MACHINE_PAYLOAD_RELATIVE_PATH: &str = r"ClashSharp\Service";

const ASSOCIATION_SCHEMA_VERSION: u32 = 1;
const PIPE_PREFIX: &str = "ClashSharp.Mihomo.";
const RESERVED_TEMPLATE_FRAGMENT: &str = "@@CLASHSHARP_";

/// High-level non-privileged installer action.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OperationAction {
    /// Install/update the current user's package and provision the service when ownership permits.
    Install,
    /// Explicitly repair/update and, when necessary, re-associate the machine service.
    Repair,
    /// Remove the current user's package and machine resources only when this user owns them.
    Uninstall,
}

/// One ordered operation step used by the interactive installer.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OperationStep {
    /// Install the package certificate in the target user's CurrentUser store.
    InstallCurrentUserCertificate,
    /// Add/update the target user's MSIX without removing LocalState first.
    DeployCurrentUserPackageInPlace,
    /// Durably reserve an ordinary machine transaction before any CurrentUser mutation.
    PrepareMachineInstall,
    /// Durably reserve an explicit cross-SID repair before any CurrentUser mutation.
    PrepareMachineRepair,
    /// Independently verify the deployed package and roll the reserved machine transaction forward.
    CommitMachineTransaction,
    /// Remove machine resources only if the association belongs to the target SID.
    RemoveMachineResourcesIfOwner,
    /// Remove the current user's startup fallback.
    RemoveCurrentUserStartupFallback,
    /// Remove the current user's package when present.
    RemoveCurrentUserPackageIfPresent,
    /// Verify package absence and clear the durable uninstall recovery transaction.
    FinalizeUninstallTransaction,
}

/// Builds the only supported operation order for an installer action.
#[must_use]
pub fn operation_steps(action: OperationAction) -> &'static [OperationStep] {
    match action {
        OperationAction::Install => &[
            OperationStep::PrepareMachineInstall,
            OperationStep::InstallCurrentUserCertificate,
            OperationStep::DeployCurrentUserPackageInPlace,
            OperationStep::CommitMachineTransaction,
        ],
        OperationAction::Repair => &[
            OperationStep::PrepareMachineRepair,
            OperationStep::InstallCurrentUserCertificate,
            OperationStep::DeployCurrentUserPackageInPlace,
            OperationStep::CommitMachineTransaction,
        ],
        OperationAction::Uninstall => &[
            OperationStep::RemoveMachineResourcesIfOwner,
            OperationStep::RemoveCurrentUserStartupFallback,
            OperationStep::RemoveCurrentUserPackageIfPresent,
            OperationStep::FinalizeUninstallTransaction,
        ],
    }
}

/// Strict invocation accepted by the elevated copy of this executable.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MachineHelperInvocation {
    /// Durably reserves an install that must not take ownership from another SID.
    PrepareInstall { target_sid: String },
    /// Durably reserves an explicit repair that may re-associate ownership.
    PrepareRepair { target_sid: String },
    /// Verifies CurrentUser package commit and rolls the reserved machine state forward.
    Commit { target_sid: String },
    /// Owner-checked machine uninstall.
    Uninstall { target_sid: String },
    /// Verifies uninstall completion and clears its durable recovery transaction.
    FinalizeUninstall { target_sid: String },
}

impl MachineHelperInvocation {
    /// Parses an exact helper command line and rejects paths, unknown flags, and trailing arguments.
    pub fn parse(arguments: &[String]) -> Result<Option<Self>, String> {
        let Some(mode) = arguments.first().map(String::as_str) else {
            return Ok(None);
        };

        let invocation = match mode {
            "--machine-prepare-install"
                if arguments.len() == 3 && arguments[1] == "--target-sid" =>
            {
                validate_owner_sid(&arguments[2])?;
                Self::PrepareInstall {
                    target_sid: arguments[2].clone(),
                }
            }
            "--machine-prepare-repair"
                if arguments.len() == 3 && arguments[1] == "--target-sid" =>
            {
                validate_owner_sid(&arguments[2])?;
                Self::PrepareRepair {
                    target_sid: arguments[2].clone(),
                }
            }
            "--machine-commit" if arguments.len() == 3 && arguments[1] == "--target-sid" => {
                validate_owner_sid(&arguments[2])?;
                Self::Commit {
                    target_sid: arguments[2].clone(),
                }
            }
            "--machine-uninstall" if arguments.len() == 3 && arguments[1] == "--target-sid" => {
                validate_owner_sid(&arguments[2])?;
                Self::Uninstall {
                    target_sid: arguments[2].clone(),
                }
            }
            "--machine-finalize-uninstall"
                if arguments.len() == 3 && arguments[1] == "--target-sid" =>
            {
                validate_owner_sid(&arguments[2])?;
                Self::FinalizeUninstall {
                    target_sid: arguments[2].clone(),
                }
            }
            value if value.starts_with("--machine-") => {
                return Err(String::from("installer.machine_helper.arguments_invalid"));
            }
            _ => return Ok(None),
        };

        Ok(Some(invocation))
    }

    /// Returns the target interactive owner SID.
    #[must_use]
    pub fn target_sid(&self) -> &str {
        match self {
            Self::PrepareInstall { target_sid }
            | Self::PrepareRepair { target_sid }
            | Self::Commit { target_sid }
            | Self::Uninstall { target_sid }
            | Self::FinalizeUninstall { target_sid } => target_sid,
        }
    }
}

/// Strict machine-owned association shared with the application.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct MachineAssociation {
    schema_version: u32,
    owner_sid: String,
    authentication_token: String,
}

impl MachineAssociation {
    /// Creates a validated association.
    pub fn new(owner_sid: &str, authentication_token: &str) -> Result<Self, String> {
        validate_owner_sid(owner_sid)?;
        validate_token(authentication_token)?;
        Ok(Self {
            schema_version: ASSOCIATION_SCHEMA_VERSION,
            owner_sid: owner_sid.to_owned(),
            authentication_token: authentication_token.to_owned(),
        })
    }

    /// Parses a strict, bounded association document.
    pub fn parse(bytes: &[u8]) -> Result<Self, String> {
        if bytes.is_empty() || bytes.len() > 4096 {
            return Err(String::from("installer.association.size_invalid"));
        }

        let value: Self = serde_json::from_slice(bytes)
            .map_err(|_| String::from("installer.association.json_invalid"))?;
        if value.schema_version != ASSOCIATION_SCHEMA_VERSION {
            return Err(String::from("installer.association.schema_invalid"));
        }
        validate_owner_sid(&value.owner_sid)?;
        validate_token(&value.authentication_token)?;
        Ok(value)
    }

    /// Serializes the canonical three-field JSON document.
    pub fn to_json(&self) -> Result<String, String> {
        serde_json::to_string(self)
            .map_err(|_| String::from("installer.association.serialize_failed"))
    }

    /// Gets the associated target SID.
    #[must_use]
    pub fn owner_sid(&self) -> &str {
        &self.owner_sid
    }

    /// Gets the deployment token.
    #[must_use]
    pub fn authentication_token(&self) -> &str {
        &self.authentication_token
    }
}

/// Result of reading a fixed machine association path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AssociationState {
    /// No association file exists.
    Missing,
    /// The association exists and is strict/valid.
    Valid(MachineAssociation),
    /// A file exists but is unsafe or invalid.
    Invalid,
}

/// Apply policy result resolved before any machine mutation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ApplyDecision {
    /// Provision with this token.
    Provision { authentication_token: String },
    /// A different or indeterminate owner exists; ordinary Install must not take it over.
    RequiresExplicitRepair,
}

/// Resolves install/repair ownership for one explicit target SID.
pub fn decide_apply_for_owner(
    target_sid: &str,
    allow_reassociation: bool,
    association: &AssociationState,
    service_exists: bool,
    machine_residue_exists: bool,
    fresh_token: &str,
) -> Result<ApplyDecision, String> {
    validate_owner_sid(target_sid)?;
    validate_token(fresh_token)?;

    match association {
        AssociationState::Valid(value) if value.owner_sid() == target_sid => {
            Ok(ApplyDecision::Provision {
                authentication_token: value.authentication_token().to_owned(),
            })
        }
        AssociationState::Valid(_) if !allow_reassociation => {
            Ok(ApplyDecision::RequiresExplicitRepair)
        }
        AssociationState::Invalid if !allow_reassociation => {
            Ok(ApplyDecision::RequiresExplicitRepair)
        }
        AssociationState::Missing
            if (service_exists || machine_residue_exists) && !allow_reassociation =>
        {
            Ok(ApplyDecision::RequiresExplicitRepair)
        }
        AssociationState::Valid(_) | AssociationState::Missing | AssociationState::Invalid => {
            Ok(ApplyDecision::Provision {
                authentication_token: fresh_token.to_owned(),
            })
        }
    }
}

/// Returns whether owner-checked uninstall may delete machine resources.
pub fn may_uninstall_machine(
    target_sid: &str,
    association: &AssociationState,
) -> Result<bool, String> {
    validate_owner_sid(target_sid)?;
    Ok(matches!(
        association,
        AssociationState::Valid(value) if value.owner_sid() == target_sid
    ))
}

/// Target-user package facts returned from fixed Windows package/profile queries.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TargetPackageRegistration {
    install_location: PathBuf,
    package_full_name: String,
    package_family_name: String,
    publisher: String,
    publisher_id: String,
    signature_kind: String,
    is_development_mode: bool,
    profile_path: PathBuf,
}

impl TargetPackageRegistration {
    /// Parses and validates target package facts emitted by the elevated helper query.
    pub fn parse_json(text: &str) -> Result<Self, String> {
        let value: Self = serde_json::from_str(text)
            .map_err(|_| String::from("installer.target_package.query_invalid"))?;
        value.validate()?;
        Ok(value)
    }

    /// Gets the registered, identity-validated package install root.
    #[must_use]
    pub fn install_location(&self) -> &Path {
        &self.install_location
    }

    /// Gets the exact Windows package full name returned for the target user.
    #[must_use]
    pub fn package_full_name(&self) -> &str {
        &self.package_full_name
    }

    /// Extracts the canonical four-component version from the validated package full name.
    pub fn package_version(&self) -> Result<&str, String> {
        package_version_from_full_name(&self.package_full_name)
    }

    /// Compares this registration with a canonical package version embedded by the trust anchor.
    pub fn matches_trusted_package_version(&self, trusted_version: &str) -> Result<bool, String> {
        if !is_canonical_package_version(trusted_version) {
            return Err(String::from("installer.trust.package_version_invalid"));
        }
        Ok(self.package_version()? == trusted_version)
    }

    fn validate(&self) -> Result<(), String> {
        validate_absolute_path(
            &self.install_location,
            "installer.target_package.install_location_invalid",
        )?;
        validate_absolute_path(
            &self.profile_path,
            "installer.target_package.profile_path_invalid",
        )?;
        if self.package_family_name != PACKAGE_FAMILY_NAME
            || self.publisher != PACKAGE_PUBLISHER
            || self.publisher_id != PACKAGE_PUBLISHER_ID
            || self.is_development_mode
            || !matches!(
                self.signature_kind.as_str(),
                "Developer" | "Enterprise" | "Store" | "System"
            )
        {
            return Err(String::from("installer.target_package.identity_invalid"));
        }
        let expected_prefix = format!("{PACKAGE_IDENTITY_NAME}_");
        let expected_suffix = format!("_{PACKAGE_PUBLISHER_ID}");
        if !self.package_full_name.starts_with(&expected_prefix)
            || !self.package_full_name.ends_with(&expected_suffix)
            || !self
                .package_full_name
                .chars()
                .all(|value| value.is_ascii_alphanumeric() || matches!(value, '.' | '_' | '-'))
            || !self
                .install_location
                .file_name()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case(&self.package_full_name))
        {
            return Err(String::from("installer.target_package.full_name_invalid"));
        }
        self.package_version()?;
        Ok(())
    }
}

fn package_version_from_full_name(package_full_name: &str) -> Result<&str, String> {
    let expected_prefix = format!("{PACKAGE_IDENTITY_NAME}_");
    let expected_suffix = format!("_{PACKAGE_PUBLISHER_ID}");
    let components = package_full_name
        .strip_prefix(&expected_prefix)
        .and_then(|value| value.strip_suffix(&expected_suffix))
        .map(|value| value.split('_').collect::<Vec<_>>())
        .ok_or_else(|| String::from("installer.target_package.full_name_invalid"))?;
    let [version, architecture, resource_id] = components.as_slice() else {
        return Err(String::from("installer.target_package.full_name_invalid"));
    };
    if *architecture != PACKAGE_ARCHITECTURE
        || !resource_id.is_empty()
        || !is_canonical_package_version(version)
    {
        return Err(String::from("installer.target_package.full_name_invalid"));
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

/// Fully derived service deployment plan. No path comes from helper CLI arguments.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MachineServicePlan {
    transaction_id: String,
    target_sid: String,
    previous_owner_sid: Option<String>,
    authentication_token: String,
    pipe_name: String,
    registered_install_root: PathBuf,
    trusted_msix_path: PathBuf,
    trusted_msix_sha256: String,
    trusted_manifest_json: String,
    program_files_root: PathBuf,
    common_application_data_root: PathBuf,
    machine_root: PathBuf,
    machine_current_root: PathBuf,
    machine_host_path: PathBuf,
    machine_mihomo_path: PathBuf,
    config_path: PathBuf,
    service_data_root: PathBuf,
    association_path: PathBuf,
}

/// Hash-anchored local MSIX and its exact machine-payload manifest.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MachinePayloadSource<'a> {
    msix_path: &'a Path,
    msix_sha256: &'a str,
    manifest_json: &'a str,
}

/// Immutable transaction identity and ownership facts used to derive the machine plan.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MachineTransactionContext<'a> {
    transaction_id: &'a str,
    target_sid: &'a str,
    previous_owner_sid: Option<&'a str>,
    authentication_token: &'a str,
}

impl<'a> MachineTransactionContext<'a> {
    /// Groups the narrow durable-transaction inputs accepted by machine-plan construction.
    #[must_use]
    pub const fn new(
        transaction_id: &'a str,
        target_sid: &'a str,
        previous_owner_sid: Option<&'a str>,
        authentication_token: &'a str,
    ) -> Self {
        Self {
            transaction_id,
            target_sid,
            previous_owner_sid,
            authentication_token,
        }
    }
}

impl<'a> MachinePayloadSource<'a> {
    /// Groups the three inseparable inputs used to stage and re-verify machine payload files.
    #[must_use]
    pub const fn new(msix_path: &'a Path, msix_sha256: &'a str, manifest_json: &'a str) -> Self {
        Self {
            msix_path,
            msix_sha256,
            manifest_json,
        }
    }
}

/// Fixed machine roots used for preparation, association reads, and owner-checked removal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MachineResourcePlan {
    program_files_root: PathBuf,
    common_application_data_root: PathBuf,
    machine_root: PathBuf,
    service_data_root: PathBuf,
    association_path: PathBuf,
}

impl MachineResourcePlan {
    /// Derives machine paths exclusively from well-known system folders.
    pub fn new(program_files: &Path, common_application_data: &Path) -> Result<Self, String> {
        validate_absolute_path(program_files, "installer.machine.program_files_invalid")?;
        validate_absolute_path(
            common_application_data,
            "installer.machine.common_application_data_invalid",
        )?;
        Ok(Self {
            program_files_root: program_files.to_path_buf(),
            common_application_data_root: common_application_data.to_path_buf(),
            machine_root: program_files.join(MACHINE_PAYLOAD_RELATIVE_PATH),
            service_data_root: common_application_data.join(r"ClashSharp\MihomoService"),
            association_path: common_application_data.join(ASSOCIATION_RELATIVE_PATH),
        })
    }

    /// Gets the fixed Program Files payload root.
    #[must_use]
    pub fn machine_root(&self) -> &Path {
        &self.machine_root
    }

    /// Gets the trusted Program Files root used for ancestor checks.
    #[must_use]
    pub fn program_files_root(&self) -> &Path {
        &self.program_files_root
    }

    /// Gets the trusted CommonApplicationData root used for ancestor checks.
    #[must_use]
    pub fn common_application_data_root(&self) -> &Path {
        &self.common_application_data_root
    }

    /// Gets the fixed ProgramData association path.
    #[must_use]
    pub fn association_path(&self) -> &Path {
        &self.association_path
    }

    /// Gets the fixed ProgramData service root.
    #[must_use]
    pub fn service_data_root(&self) -> &Path {
        &self.service_data_root
    }

    /// Deletes only the fixed service, machine payload, and service-owned ProgramData root.
    pub fn render_uninstall_script(
        &self,
        target_sid: &str,
        transaction_path: &Path,
        transaction_id: &str,
        expected_package_version: &str,
        installer_payload_sha256: &str,
    ) -> Result<String, String> {
        validate_owner_sid(target_sid)?;
        validate_absolute_path(transaction_path, "installer.transaction.path_invalid")?;
        validate_token(transaction_id)
            .map_err(|_| String::from("installer.transaction.id_invalid"))?;
        if !is_canonical_package_version(expected_package_version) {
            return Err(String::from(
                "installer.transaction.package_version_invalid",
            ));
        }
        validate_token(installer_payload_sha256)
            .map_err(|_| String::from("installer.transaction.payload_hash_invalid"))?;
        Ok(render_template(
            UNINSTALL_SCRIPT_TEMPLATE,
            &[
                ("@@CLASHSHARP_SERVICE_NAME@@", SERVICE_NAME),
                ("@@CLASHSHARP_TARGET_SID@@", target_sid),
                (
                    "@@CLASHSHARP_PACKAGE_IDENTITY_NAME@@",
                    PACKAGE_IDENTITY_NAME,
                ),
                ("@@CLASHSHARP_PACKAGE_FAMILY_NAME@@", PACKAGE_FAMILY_NAME),
                (
                    "@@CLASHSHARP_PROGRAM_FILES_ROOT@@",
                    path_text(&self.program_files_root)?,
                ),
                (
                    "@@CLASHSHARP_PROGRAM_DATA_ROOT@@",
                    path_text(&self.common_application_data_root)?,
                ),
                (
                    "@@CLASHSHARP_MACHINE_ROOT@@",
                    path_text(&self.machine_root)?,
                ),
                (
                    "@@CLASHSHARP_SERVICE_DATA_ROOT@@",
                    path_text(&self.service_data_root)?,
                ),
                (
                    "@@CLASHSHARP_ASSOCIATION_PATH@@",
                    path_text(&self.association_path)?,
                ),
                (
                    "@@CLASHSHARP_TRANSACTION_PATH@@",
                    path_text(transaction_path)?,
                ),
                ("@@CLASHSHARP_TRANSACTION_ID@@", transaction_id),
                (
                    "@@CLASHSHARP_EXPECTED_PACKAGE_VERSION@@",
                    expected_package_version,
                ),
                (
                    "@@CLASHSHARP_INSTALLER_PAYLOAD_SHA256@@",
                    installer_payload_sha256,
                ),
            ],
        ))
    }
}

impl MachineServicePlan {
    /// Derives all fixed paths from a trusted package registration and machine folders.
    pub fn new(
        registration: &TargetPackageRegistration,
        program_files: &Path,
        common_application_data: &Path,
        transaction: MachineTransactionContext<'_>,
        payload_source: MachinePayloadSource<'_>,
    ) -> Result<Self, String> {
        let MachineTransactionContext {
            transaction_id,
            target_sid,
            previous_owner_sid,
            authentication_token,
        } = transaction;
        registration.validate()?;
        validate_absolute_path(program_files, "installer.machine.program_files_invalid")?;
        validate_absolute_path(
            common_application_data,
            "installer.machine.common_application_data_invalid",
        )?;
        validate_token(transaction_id)
            .map_err(|_| String::from("installer.transaction.id_invalid"))?;
        validate_owner_sid(target_sid)?;
        if let Some(previous_owner_sid) = previous_owner_sid {
            validate_owner_sid(previous_owner_sid)?;
            if previous_owner_sid == target_sid {
                return Err(String::from(
                    "installer.machine.previous_owner_not_distinct",
                ));
            }
        }
        validate_token(authentication_token)?;
        validate_absolute_path(
            payload_source.msix_path,
            "installer.trust.msix_path_invalid",
        )?;
        if payload_source.manifest_json.is_empty()
            || payload_source.manifest_json.len() > 1024 * 1024
        {
            return Err(String::from("installer.trust.manifest_invalid"));
        }
        if payload_source.msix_sha256.len() != 64
            || !payload_source
                .msix_sha256
                .bytes()
                .all(|value| value.is_ascii_digit() || (b'a'..=b'f').contains(&value))
        {
            return Err(String::from("installer.trust.msix_hash_invalid"));
        }

        let registered_install_root = registration.install_location.clone();
        let machine_root = program_files.join(MACHINE_PAYLOAD_RELATIVE_PATH);
        let machine_current_root = machine_root.join("current");
        let machine_host_path = machine_current_root
            .join("Host")
            .join("ClashSharp.MihomoService.exe");
        let machine_mihomo_path = machine_current_root.join("mihomo.exe");
        let config_path = registration
            .profile_path
            .join(r"AppData\Local\Packages")
            .join(&registration.package_family_name)
            .join(r"LocalState\mihomo\config.yaml");
        let service_data_root = common_application_data.join(r"ClashSharp\MihomoService");
        let association_path = common_application_data.join(ASSOCIATION_RELATIVE_PATH);
        let pipe_name = build_pipe_name(target_sid, authentication_token)?;

        for path in [
            &registered_install_root,
            payload_source.msix_path,
            &machine_root,
            &machine_current_root,
            &machine_host_path,
            &machine_mihomo_path,
            &config_path,
            &service_data_root,
            &association_path,
        ] {
            validate_absolute_path(path, "installer.machine.derived_path_invalid")?;
        }

        Ok(Self {
            transaction_id: transaction_id.to_owned(),
            target_sid: target_sid.to_owned(),
            previous_owner_sid: previous_owner_sid.map(str::to_owned),
            authentication_token: authentication_token.to_owned(),
            pipe_name,
            registered_install_root,
            trusted_msix_path: payload_source.msix_path.to_path_buf(),
            trusted_msix_sha256: payload_source.msix_sha256.to_owned(),
            trusted_manifest_json: payload_source.manifest_json.to_owned(),
            program_files_root: program_files.to_path_buf(),
            common_application_data_root: common_application_data.to_path_buf(),
            machine_root,
            machine_current_root,
            machine_host_path,
            machine_mihomo_path,
            config_path,
            service_data_root,
            association_path,
        })
    }

    /// Gets the identity-validated registered package root used for target-user facts only.
    #[must_use]
    pub fn registered_install_root(&self) -> &Path {
        &self.registered_install_root
    }

    /// Gets the hash-anchored sibling MSIX used as the only machine payload source.
    #[must_use]
    pub fn trusted_msix_path(&self) -> &Path {
        &self.trusted_msix_path
    }

    /// Gets the stable Program Files root.
    #[must_use]
    pub fn machine_root(&self) -> &Path {
        &self.machine_root
    }

    /// Gets the fixed ProgramData service-data root.
    #[must_use]
    pub fn service_data_root(&self) -> &Path {
        &self.service_data_root
    }

    /// Gets the fixed machine association path.
    #[must_use]
    pub fn association_path(&self) -> &Path {
        &self.association_path
    }

    /// Gets the target user configuration path embedded in SCM.
    #[must_use]
    pub fn config_path(&self) -> &Path {
        &self.config_path
    }

    /// Builds the exact service command line stored in SCM.
    pub fn service_binary_path(&self) -> Result<String, String> {
        Ok(format!(
            "{} --mihomo {} --config {} --pipe-name {} --ipc-token {} --allowed-sid {}",
            windows_quote(&self.machine_host_path)?,
            windows_quote(&self.machine_mihomo_path)?,
            windows_quote(&self.config_path)?,
            windows_quote_text(&self.pipe_name)?,
            windows_quote_text(&self.authentication_token)?,
            windows_quote_text(&self.target_sid)?,
        ))
    }

    /// Renders the elevated local-only payload swap, service configuration, and association transaction.
    pub fn render_apply_script(&self) -> Result<String, String> {
        let association = MachineAssociation::new(&self.target_sid, &self.authentication_token)?;
        let service_binary_path = self.service_binary_path()?;
        Ok(render_template(
            APPLY_SCRIPT_TEMPLATE,
            &[
                ("@@CLASHSHARP_SERVICE_NAME@@", SERVICE_NAME),
                ("@@CLASHSHARP_DISPLAY_NAME@@", SERVICE_DISPLAY_NAME),
                ("@@CLASHSHARP_TRANSACTION_ID@@", &self.transaction_id),
                ("@@CLASHSHARP_TARGET_SID@@", &self.target_sid),
                (
                    "@@CLASHSHARP_PREVIOUS_OWNER_SID@@",
                    self.previous_owner_sid.as_deref().unwrap_or(""),
                ),
                (
                    "@@CLASHSHARP_PACKAGE_IDENTITY_NAME@@",
                    PACKAGE_IDENTITY_NAME,
                ),
                ("@@CLASHSHARP_PACKAGE_FAMILY_NAME@@", PACKAGE_FAMILY_NAME),
                (
                    "@@CLASHSHARP_PROGRAM_FILES_ROOT@@",
                    path_text(&self.program_files_root)?,
                ),
                (
                    "@@CLASHSHARP_PROGRAM_DATA_ROOT@@",
                    path_text(&self.common_application_data_root)?,
                ),
                (
                    "@@CLASHSHARP_REGISTERED_ROOT@@",
                    path_text(&self.registered_install_root)?,
                ),
                (
                    "@@CLASHSHARP_TRUSTED_MSIX@@",
                    path_text(&self.trusted_msix_path)?,
                ),
                (
                    "@@CLASHSHARP_TRUSTED_MSIX_SHA256@@",
                    &self.trusted_msix_sha256,
                ),
                (
                    "@@CLASHSHARP_TRUSTED_MANIFEST@@",
                    &self.trusted_manifest_json,
                ),
                (
                    "@@CLASHSHARP_MACHINE_ROOT@@",
                    path_text(&self.machine_root)?,
                ),
                (
                    "@@CLASHSHARP_CURRENT_ROOT@@",
                    path_text(&self.machine_current_root)?,
                ),
                (
                    "@@CLASHSHARP_MACHINE_HOST@@",
                    path_text(&self.machine_host_path)?,
                ),
                (
                    "@@CLASHSHARP_MACHINE_MIHOMO@@",
                    path_text(&self.machine_mihomo_path)?,
                ),
                ("@@CLASHSHARP_CONFIG_PATH@@", path_text(&self.config_path)?),
                (
                    "@@CLASHSHARP_SERVICE_DATA_ROOT@@",
                    path_text(&self.service_data_root)?,
                ),
                (
                    "@@CLASHSHARP_ASSOCIATION_PATH@@",
                    path_text(&self.association_path)?,
                ),
                ("@@CLASHSHARP_ASSOCIATION_JSON@@", &association.to_json()?),
                ("@@CLASHSHARP_PIPE_NAME@@", &self.pipe_name),
                ("@@CLASHSHARP_TOKEN@@", &self.authentication_token),
                ("@@CLASHSHARP_SERVICE_BINARY_PATH@@", &service_binary_path),
            ],
        ))
    }
}

/// Generates a cryptographically random canonical 256-bit token.
pub fn generate_token() -> Result<String, String> {
    let mut bytes = [0_u8; 32];
    getrandom::fill(&mut bytes).map_err(|_| String::from("installer.token.random_failed"))?;
    Ok(lower_hex(&bytes))
}

/// Builds the exact owner/token-derived pipe name used by the C# protocol.
pub fn build_pipe_name(owner_sid: &str, authentication_token: &str) -> Result<String, String> {
    validate_owner_sid(owner_sid)?;
    validate_token(authentication_token)?;
    let mut hasher = Sha256::new();
    hasher.update(b"ClashSharp.Mihomo.IPC\0");
    hasher.update(owner_sid.as_bytes());
    hasher.update(b"\0");
    hasher.update(authentication_token.as_bytes());
    let digest = hasher.finalize();
    Ok(format!("{PIPE_PREFIX}{}", lower_hex(&digest[..16])))
}

/// Validates the canonical non-privileged target SID syntax accepted at the helper boundary.
pub fn validate_owner_sid(value: &str) -> Result<(), String> {
    if value.len() < 7 || value.len() > 184 || !value.starts_with("S-") {
        return Err(String::from("installer.target_sid.invalid"));
    }

    let parts = value[2..].split('-').collect::<Vec<_>>();
    if !(3..=17).contains(&parts.len())
        || parts.iter().any(|part| {
            part.is_empty()
                || !part.chars().all(|character| character.is_ascii_digit())
                || part.len() > 1 && part.starts_with('0')
        })
        || parts[0] != "1"
        || parts[1]
            .parse::<u64>()
            .map_or(true, |authority| authority > 0x0000_ffff_ffff_ffff)
        || parts[2..].iter().any(|part| part.parse::<u32>().is_err())
        || matches!(
            value,
            "S-1-1-0" | "S-1-5-2" | "S-1-5-7" | "S-1-5-18" | "S-1-5-32-544"
        )
    {
        return Err(String::from("installer.target_sid.invalid"));
    }
    Ok(())
}

/// Validates a canonical lowercase 256-bit token.
pub fn validate_token(value: &str) -> Result<(), String> {
    if value.len() != 64
        || !value
            .chars()
            .all(|character| character.is_ascii_digit() || ('a'..='f').contains(&character))
    {
        return Err(String::from("installer.token.invalid"));
    }
    Ok(())
}

fn validate_absolute_path(path: &Path, code: &str) -> Result<(), String> {
    let text = path.to_string_lossy();
    if !path.is_absolute()
        || text.contains('"')
        || text.chars().any(char::is_control)
        || text.contains(RESERVED_TEMPLATE_FRAGMENT)
    {
        return Err(code.to_owned());
    }
    Ok(())
}

fn path_text(path: &Path) -> Result<&str, String> {
    path.to_str()
        .ok_or_else(|| String::from("installer.machine.path_unicode_invalid"))
}

fn windows_quote(path: &Path) -> Result<String, String> {
    windows_quote_text(path_text(path)?)
}

fn windows_quote_text(value: &str) -> Result<String, String> {
    if value.contains('"') || value.chars().any(char::is_control) {
        return Err(String::from("installer.machine.command_argument_invalid"));
    }
    Ok(format!("\"{value}\""))
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

fn render_template(template: &str, replacements: &[(&str, &str)]) -> String {
    let mut result = template.to_owned();
    for (placeholder, value) in replacements {
        result = result.replace(placeholder, &powershell_literal(value));
    }
    debug_assert!(!result.contains(RESERVED_TEMPLATE_FRAGMENT));
    result
}

fn powershell_literal(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

const APPLY_SCRIPT_TEMPLATE: &str = r#"
$ErrorActionPreference = 'Stop'
$rollForwardOnly = $true
$rootsPrepared = $false
$mutationStarted = $false
$committed = $false
$payloadSwapped = $false
$oldCurrentBackedUp = $false
$oldServiceExisted = $false
$oldServiceWasRunning = $false
$oldAssociationExisted = $false
$associationChanged = $false
$stageRoot = $null
$packageStage = $null
$backupRoot = $null
$associationTemp = $null
$associationBackupPath = $null
try {
    $serviceName = @@CLASHSHARP_SERVICE_NAME@@
    $displayName = @@CLASHSHARP_DISPLAY_NAME@@
    $targetSidText = @@CLASHSHARP_TARGET_SID@@
    $previousOwnerSidText = @@CLASHSHARP_PREVIOUS_OWNER_SID@@
    $packageIdentityName = @@CLASHSHARP_PACKAGE_IDENTITY_NAME@@
    $expectedFamilyName = @@CLASHSHARP_PACKAGE_FAMILY_NAME@@
    $programFilesRoot = @@CLASHSHARP_PROGRAM_FILES_ROOT@@
    $programDataRoot = @@CLASHSHARP_PROGRAM_DATA_ROOT@@
    $registeredRoot = @@CLASHSHARP_REGISTERED_ROOT@@
    $trustedMsix = @@CLASHSHARP_TRUSTED_MSIX@@
    $trustedMsixSha256 = @@CLASHSHARP_TRUSTED_MSIX_SHA256@@
    $trustedManifestJson = @@CLASHSHARP_TRUSTED_MANIFEST@@
    $machineRoot = @@CLASHSHARP_MACHINE_ROOT@@
    $currentRoot = @@CLASHSHARP_CURRENT_ROOT@@
    $machineHost = @@CLASHSHARP_MACHINE_HOST@@
    $machineMihomo = @@CLASHSHARP_MACHINE_MIHOMO@@
    $configPath = @@CLASHSHARP_CONFIG_PATH@@
    $serviceDataRoot = @@CLASHSHARP_SERVICE_DATA_ROOT@@
    $associationPath = @@CLASHSHARP_ASSOCIATION_PATH@@
    $associationJson = @@CLASHSHARP_ASSOCIATION_JSON@@
    $pipeName = @@CLASHSHARP_PIPE_NAME@@
    $ipcToken = @@CLASHSHARP_TOKEN@@
    $serviceBinaryPath = @@CLASHSHARP_SERVICE_BINARY_PATH@@
    $scExe = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
    $productFilesRoot = Split-Path -Parent $machineRoot
    $productDataRoot = Split-Path -Parent $serviceDataRoot

    function Assert-TargetPackageQuiescent(
        [string] $InstallRoot,
        [string] $OwnerSid) {
        $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([char]92)
        if (-not [IO.Path]::IsPathRooted($root)) {
            throw 'registered package process root is invalid'
        }
        $prefix = $root + [IO.Path]::DirectorySeparatorChar
        foreach ($process in @(Get-CimInstance -ClassName Win32_Process)) {
            $candidate = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
            try {
                $candidate = [IO.Path]::GetFullPath($candidate)
            } catch {
                if ($candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "registered package process path is ambiguous: $($process.ProcessId)"
                }
                continue
            }
            if (-not $candidate.StartsWith(
                    $prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            try {
                $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
            } catch {
                $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                    'ProcessId = ' + [uint32]$process.ProcessId))
                if ($remaining.Count -eq 0) { continue }
                throw "registered package process token is unreadable: $($process.ProcessId)"
            }
            if ($null -eq $owner -or [uint32]$owner.ReturnValue -ne 0 -or
                [string]::IsNullOrWhiteSpace([string]$owner.Sid)) {
                $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                    'ProcessId = ' + [uint32]$process.ProcessId))
                if ($remaining.Count -eq 0) { continue }
                throw "registered package process token is ambiguous: $($process.ProcessId)"
            }
            if ([string]$owner.Sid -ceq $OwnerSid) {
                throw "installer.app.running: pid=$($process.ProcessId)"
            }
        }
    }

    function Get-ExactPackageInstallRoot([string] $OwnerSid) {
        $packages = @(Get-AppxPackage -User $OwnerSid -Name $packageIdentityName)
        if ($packages.Count -eq 0) { return $null }
        if ($packages.Count -ne 1) { throw 'owner package registration is ambiguous' }
        $package = $packages[0]
        $packageFullName = [string]$package.PackageFullName
        $installRoot = [IO.Path]::GetFullPath(
            [string]$package.InstallLocation).TrimEnd([char]92)
        if ([string]::IsNullOrWhiteSpace($packageFullName) -or
            [string]::IsNullOrWhiteSpace($installRoot) -or
            [string]$package.Name -cne $packageIdentityName -or
            [string]$package.PackageFamilyName -cne $expectedFamilyName -or
            -not [IO.Path]::IsPathRooted($installRoot) -or
            -not ([IO.Path]::GetFileName($installRoot)).Equals(
                $packageFullName, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'owner package registration is not exact'
        }
        return $installRoot
    }

    $targetSid = [System.Security.Principal.SecurityIdentifier]::new($targetSidText)
    if ($targetSid.Value -cne $targetSidText) { throw 'target SID is not canonical' }
    $systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

    function Invoke-Sc([string[]] $Arguments) {
        & $scExe @Arguments | Out-Null
        if ($LASTEXITCODE -eq 1072) {
            throw 'installer.machine.service_delete_pending_reboot'
        }
        if ($LASTEXITCODE -ne 0) { throw "sc.exe failed with exit code $LASTEXITCODE" }
    }

    function Get-ServiceSddl() {
        $output = @(& $scExe sdshow $serviceName 2>&1)
        if ($LASTEXITCODE -eq 1072) {
            throw 'installer.machine.service_delete_pending_reboot'
        }
        if ($LASTEXITCODE -ne 0) { throw "sc.exe sdshow failed with exit code $LASTEXITCODE" }
        $sddl = $output |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { $_.StartsWith('D:', [StringComparison]::Ordinal) } |
            Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($sddl)) { throw 'service SDDL query returned no DACL' }
        return [string]$sddl
    }

    function Assert-SafeDirectoryChain([string] $Root, [string] $Target) {
        $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $targetFull = [IO.Path]::GetFullPath($Target).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
        if (-not $targetFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -and
            -not $targetFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "fixed path escaped trusted root: $Target"
        }
        $rootItem = Get-Item -LiteralPath $rootFull -Force
        if (-not $rootItem.PSIsContainer -or
            ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "trusted root is unsafe: $rootFull"
        }
        if ($targetFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return }
        $relative = $targetFull.Substring($prefix.Length)
        $current = $rootFull
        foreach ($segment in $relative.Split(
            [char[]]@([IO.Path]::DirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($segment -eq '.' -or $segment -eq '..') { throw 'unsafe path segment' }
            $current = Join-Path $current $segment
            if (-not (Test-Path -LiteralPath $current)) { continue }
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "unsafe directory ancestor: $current"
            }
        }
    }

    function Assert-NoReparseTree([string] $Path, [bool] $Leaf) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "reparse point rejected: $Path"
        }
        if ($Leaf) {
            if ($item.PSIsContainer) { throw "unexpected path kind: $Path" }
            return
        }
        if (-not $item.PSIsContainer) { throw "unexpected path kind: $Path" }
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "reparse point rejected: $($child.FullName)"
            }
        }
    }

    function Set-MachineDirectoryAcl([string] $Path, [bool] $OwnerMayRead) {
        $acl = [System.Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($administratorsSid)
        $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        $none = [System.Security.AccessControl.PropagationFlags]::None
        $allow = [System.Security.AccessControl.AccessControlType]::Allow
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $systemSid, [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $none, $allow))
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $administratorsSid, [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $none, $allow))
        if ($OwnerMayRead) {
            $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                $targetSid, [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
                $inheritance, $none, $allow))
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    }

    function Set-AssociationFileAcl([string] $Path) {
        $acl = [System.Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($administratorsSid)
        $allow = [System.Security.AccessControl.AccessControlType]::Allow
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $systemSid, [System.Security.AccessControl.FileSystemRights]::FullControl, $allow))
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $administratorsSid, [System.Security.AccessControl.FileSystemRights]::FullControl, $allow))
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $targetSid, [System.Security.AccessControl.FileSystemRights]::Read, $allow))
        Set-Acl -LiteralPath $Path -AclObject $acl
    }

    function Restore-DirectoryState(
        [string] $Path,
        [bool] $Existed,
        [System.Security.AccessControl.DirectorySecurity] $Acl) {
        if ($Existed) {
            if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
                throw "rollback directory is missing: $Path"
            }
            Assert-NoReparseTree $Path $false
            Set-Acl -LiteralPath $Path -AclObject $Acl
        } elseif (Test-Path -LiteralPath $Path) {
            Assert-NoReparseTree $Path $false
            Remove-Item -LiteralPath $Path -Recurse -Force
        }
    }

    function Get-StagedRelativePath([string] $PackagePath) {
        if ($PackagePath -ceq 'binaries/mihomo.exe') { return 'mihomo.exe' }
        if ($PackagePath.StartsWith('binaries/service/', [StringComparison]::Ordinal)) {
            return 'Host' + [IO.Path]::DirectorySeparatorChar +
                $PackagePath.Substring('binaries/service/'.Length).Replace(
                    [char]'/', [IO.Path]::DirectorySeparatorChar)
        }
        if ($PackagePath.StartsWith('binaries/geodata/', [StringComparison]::Ordinal)) {
            return 'GeoData' + [IO.Path]::DirectorySeparatorChar +
                $PackagePath.Substring('binaries/geodata/'.Length).Replace(
                    [char]'/', [IO.Path]::DirectorySeparatorChar)
        }
        throw "machine manifest path is outside the fixed payload set: $PackagePath"
    }

    function Get-StagedPath([string] $Root, [string] $PackagePath) {
        $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull (Get-StagedRelativePath $PackagePath)))
        if (-not $candidate.StartsWith(
            $rootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'staged payload path escaped its protected root'
        }
        return $candidate
    }

    function Assert-StagedPayload([string] $Root) {
        Assert-NoReparseTree $Root $false
        $actualFiles = @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse)
        if ($actualFiles.Count -ne $manifestByPath.Count) {
            throw 'staged payload exact file set mismatch'
        }
        foreach ($entry in $trustedManifest) {
            $path = Get-StagedPath $Root ([string]$entry.path)
            $item = Get-Item -LiteralPath $path -Force
            if ($item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $item.Length -ne [long]$entry.length) {
                throw "staged payload metadata mismatch: $($entry.path)"
            }
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $hash.Equals([string]$entry.sha256, [StringComparison]::Ordinal)) {
                throw "staged payload hash mismatch: $($entry.path)"
            }
        }
    }

    function Stop-ServiceAndWait() {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
            $service.Refresh()
            if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                throw 'service did not stop within 30 seconds'
            }
        }
    }

    function Remove-ServiceAndWait() {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $serviceName -Force
                $service.WaitForStatus(
                    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                    [TimeSpan]::FromSeconds(30))
            }
            & $scExe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0 -and
                $LASTEXITCODE -ne 1060 -and
                $LASTEXITCODE -ne 1072) {
                throw "sc.exe delete failed with exit code $LASTEXITCODE"
            }
            $service.Dispose()
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($true) {
            & $scExe query $serviceName | Out-Null
            if ($LASTEXITCODE -eq 1060) { return }
            if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1072) {
                throw "sc.exe query failed with exit code $LASTEXITCODE"
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw 'installer.machine.service_delete_pending_reboot'
            }
            Start-Sleep -Milliseconds 100
        }
    }

    if (-not ([IO.Path]::GetFullPath($productFilesRoot)).Equals(
            [IO.Path]::GetFullPath((Join-Path $programFilesRoot 'ClashSharp')),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFullPath($productDataRoot)).Equals(
            [IO.Path]::GetFullPath((Join-Path $programDataRoot 'ClashSharp')),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'derived product roots are not fixed descendants'
    }
    Assert-SafeDirectoryChain $programFilesRoot $machineRoot
    Assert-SafeDirectoryChain $programDataRoot $serviceDataRoot

    $productFilesExisted = Test-Path -LiteralPath $productFilesRoot -PathType Container
    $machineRootExisted = Test-Path -LiteralPath $machineRoot -PathType Container
    $productDataExisted = Test-Path -LiteralPath $productDataRoot -PathType Container
    $serviceDataExisted = Test-Path -LiteralPath $serviceDataRoot -PathType Container
    $productFilesAclBefore = if ($productFilesExisted) { Get-Acl -LiteralPath $productFilesRoot } else { $null }
    $machineRootAclBefore = if ($machineRootExisted) { Get-Acl -LiteralPath $machineRoot } else { $null }
    $productDataAclBefore = if ($productDataExisted) { Get-Acl -LiteralPath $productDataRoot } else { $null }
    $serviceDataAclBefore = if ($serviceDataExisted) { Get-Acl -LiteralPath $serviceDataRoot } else { $null }

    $rootsPrepared = $true
    [IO.Directory]::CreateDirectory($productFilesRoot) | Out-Null
    Assert-SafeDirectoryChain $programFilesRoot $productFilesRoot
    Set-MachineDirectoryAcl $productFilesRoot $false
    [IO.Directory]::CreateDirectory($machineRoot) | Out-Null
    Assert-SafeDirectoryChain $programFilesRoot $machineRoot
    Set-MachineDirectoryAcl $machineRoot $false
    [IO.Directory]::CreateDirectory($productDataRoot) | Out-Null
    Assert-SafeDirectoryChain $programDataRoot $productDataRoot
    Set-MachineDirectoryAcl $productDataRoot $true
    [IO.Directory]::CreateDirectory($serviceDataRoot) | Out-Null
    Assert-SafeDirectoryChain $programDataRoot $serviceDataRoot
    Set-MachineDirectoryAcl $serviceDataRoot $true

    $trustedManifest = @($trustedManifestJson | ConvertFrom-Json)
    if ($trustedManifest.Count -lt 3 -or $trustedManifest.Count -gt 4096) {
        throw 'trusted machine manifest entry count is invalid'
    }
    $manifestByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $manifestBytes = [long]0
    foreach ($entry in $trustedManifest) {
        $propertyNames = @($entry.PSObject.Properties.Name)
        $path = [string]$entry.path
        $length = [long]$entry.length
        $sha256 = [string]$entry.sha256
        if ($propertyNames.Count -ne 3 -or
            $propertyNames -cnotcontains 'path' -or
            $propertyNames -cnotcontains 'length' -or
            $propertyNames -cnotcontains 'sha256' -or
            $path -cnotmatch '^binaries/(mihomo\.exe|service/[a-z0-9._-]+(?:/[a-z0-9._-]+)*|geodata/[a-z0-9._-]+(?:/[a-z0-9._-]+)*)$' -or
            $length -lt 1 -or $length -gt 536870912 -or
            $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $manifestByPath.ContainsKey($path)) {
            throw "trusted machine manifest entry is invalid: $path"
        }
        $manifestByPath.Add($path, $entry)
        $manifestBytes += $length
        if ($manifestBytes -gt 1073741824) { throw 'trusted machine manifest exceeds size budget' }
    }

    $registeredItem = Get-Item -LiteralPath $registeredRoot -Force
    if (-not $registeredItem.PSIsContainer -or
        ($registeredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'registered package root is unsafe'
    }
    $trustedMsixItem = Get-Item -LiteralPath $trustedMsix -Force
    if ($trustedMsixItem.PSIsContainer -or
        ($trustedMsixItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'trusted sibling MSIX is unsafe'
    }

    $nonce = @@CLASHSHARP_TRANSACTION_ID@@
    if ($nonce -cnotmatch '^[0-9a-f]{64}$') { throw 'transaction id is invalid' }
    $stageRoot = Join-Path $machineRoot ".staging-$nonce"
    $packageStage = Join-Path $machineRoot ".package-$nonce.msix"
    $backupRoot = Join-Path $machineRoot ".backup-$nonce"
    $associationTemp = Join-Path $serviceDataRoot ".association-$nonce.tmp"
    $associationBackupPath = Join-Path $serviceDataRoot ".association-$nonce.bak"
    foreach ($retryPath in @($stageRoot, $packageStage, $associationTemp)) {
        if (-not (Test-Path -LiteralPath $retryPath)) { continue }
        $retryItem = Get-Item -LiteralPath $retryPath -Force
        if (($retryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "transaction retry path is a reparse point: $retryPath"
        }
        Remove-Item -LiteralPath $retryPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $backupRoot) {
        Assert-NoReparseTree $backupRoot $false
    }
    if (Test-Path -LiteralPath $associationBackupPath) {
        Assert-NoReparseTree $associationBackupPath $true
        Remove-Item -LiteralPath $associationBackupPath -Force
    }
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    Set-MachineDirectoryAcl $stageRoot $false
    Copy-Item -LiteralPath $trustedMsix -Destination $packageStage
    Assert-NoReparseTree $packageStage $true

    Add-Type -AssemblyName System.IO.Compression
    $packageStream = [IO.File]::Open(
        $packageStage, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    $archive = $null
    try {
        $actualMsixHash = ([BitConverter]::ToString(
            $sha.ComputeHash($packageStream))).Replace('-', '').ToLowerInvariant()
        if (-not $actualMsixHash.Equals($trustedMsixSha256, [StringComparison]::Ordinal)) {
            throw 'protected MSIX whole-file hash mismatch'
        }
        $packageStream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new(
            $packageStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($zipEntry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($zipEntry.Name)) { continue }
            if ($zipEntry.FullName.Contains('\')) { throw 'MSIX machine entry uses a backslash' }
            $packagePath = $zipEntry.FullName.ToLowerInvariant()
            $isMachineEntry = $packagePath -ceq 'binaries/mihomo.exe' -or
                $packagePath.StartsWith('binaries/service/', [StringComparison]::Ordinal) -or
                $packagePath.StartsWith('binaries/geodata/', [StringComparison]::Ordinal)
            if (-not $isMachineEntry) { continue }
            if (-not $manifestByPath.ContainsKey($packagePath) -or -not $seen.Add($packagePath)) {
                throw "MSIX machine exact set mismatch: $packagePath"
            }
            $expected = $manifestByPath[$packagePath]
            if ($zipEntry.Length -ne [long]$expected.length) {
                throw "MSIX machine entry length mismatch: $packagePath"
            }
            $destination = Get-StagedPath $stageRoot $packagePath
            [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
            $inputStream = $zipEntry.Open()
            $outputStream = [IO.File]::Open(
                $destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream) } finally {
                $outputStream.Dispose()
                $inputStream.Dispose()
            }
        }
        if ($seen.Count -ne $manifestByPath.Count) { throw 'MSIX machine file set is incomplete' }
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $sha.Dispose()
        $packageStream.Dispose()
    }
    Set-MachineDirectoryAcl $stageRoot $false
    Assert-StagedPayload $stageRoot
    Remove-Item -LiteralPath $packageStage -Force
    $packageStage = $null

    $oldAssociationExisted = Test-Path -LiteralPath $associationPath -PathType Leaf
    if ($oldAssociationExisted) {
        $associationItem = Get-Item -LiteralPath $associationPath -Force
        if (($associationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $associationItem.Length -lt 1 -or $associationItem.Length -gt 4096) {
            throw 'existing association is unsafe'
        }
        $oldAssociationBytes = [IO.File]::ReadAllBytes($associationPath)
        $oldAssociationAcl = Get-Acl -LiteralPath $associationPath
    }

    if (-not [string]::IsNullOrWhiteSpace($previousOwnerSidText)) {
        $previousOwnerSid = [Security.Principal.SecurityIdentifier]::new(
            $previousOwnerSidText)
        if ($previousOwnerSid.Value -cne $previousOwnerSidText -or
            $previousOwnerSidText -ceq $targetSidText) {
            throw 'previous owner SID is not canonical and distinct'
        }
        $previousOwnerRoot = Get-ExactPackageInstallRoot $previousOwnerSidText
        if (-not [string]::IsNullOrWhiteSpace([string]$previousOwnerRoot)) {
            Assert-TargetPackageQuiescent $previousOwnerRoot $previousOwnerSidText
        }
    }
    Assert-TargetPackageQuiescent $registeredRoot $targetSidText
    $oldService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $oldServiceExisted = $null -ne $oldService
    if ($oldServiceExisted) {
        $oldServiceWasRunning = $oldService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped
        $oldServiceCim = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        if ($null -eq $oldServiceCim -or
            $oldServiceCim.StartName -cne 'LocalSystem' -or
            $oldServiceCim.ServiceType -cne 'Own Process') {
            throw 'existing service configuration cannot be safely rolled back'
        }
        $oldServiceSddl = Get-ServiceSddl
        $delayedValue = Get-ItemPropertyValue -LiteralPath (
            "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$serviceName") `
            -Name DelayedAutoStart -ErrorAction SilentlyContinue
        $oldServiceDelayed = [int]$delayedValue -eq 1
    }

    $oldCurrentExisted = Test-Path -LiteralPath $currentRoot -PathType Container
    if ($oldCurrentExisted) { Assert-NoReparseTree $currentRoot $false }

    $mutationStarted = $true

    Stop-ServiceAndWait
    $serviceFence = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $serviceFence) {
        Invoke-Sc -Arguments @('config', $serviceName, 'start=', 'disabled')
        Invoke-Sc -Arguments @(
            'sdset', $serviceName,
            'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)')
    }
    if ($oldCurrentExisted) {
        if (Test-Path -LiteralPath $backupRoot) {
            Assert-NoReparseTree $currentRoot $false
            Remove-Item -LiteralPath $currentRoot -Recurse -Force
        } else {
            Move-Item -LiteralPath $currentRoot -Destination $backupRoot
        }
        $oldCurrentBackedUp = $true
    }
    Move-Item -LiteralPath $stageRoot -Destination $currentRoot
    $payloadSwapped = $true
    $stageRoot = $null
    Set-MachineDirectoryAcl $currentRoot $false
    Assert-StagedPayload $currentRoot
    if (-not (Test-Path -LiteralPath $machineHost -PathType Leaf) -or
        -not (Test-Path -LiteralPath $machineMihomo -PathType Leaf)) {
        throw 'stable machine payload verification failed'
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Invoke-Sc -Arguments @(
            'create', $serviceName, 'binPath=', $serviceBinaryPath, 'type=', 'own',
            'start=', 'auto', 'obj=', 'LocalSystem', 'DisplayName=', $displayName)
    } else {
        Invoke-Sc -Arguments @(
            'config', $serviceName, 'binPath=', $serviceBinaryPath, 'type=', 'own',
            'start=', 'auto', 'obj=', 'LocalSystem', 'DisplayName=', $displayName)
    }
    Invoke-Sc -Arguments @('config', $serviceName, 'start=', 'delayed-auto')
    Invoke-Sc -Arguments @('description', $serviceName, 'Clash# local transparent-proxy host')
    $serviceSddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)' +
        '(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)' +
        "(A;;CCLCSWLOCRRC;;;$targetSidText)"
    Invoke-Sc -Arguments @('sdset', $serviceName, $serviceSddl)

    $associationDirectory = Split-Path -Parent $associationPath
    if (-not $associationDirectory.Equals($serviceDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'association path escaped the fixed service data root'
    }
    $associationTemp = Join-Path $associationDirectory ".association-$nonce.tmp"
    [System.IO.File]::WriteAllText(
        $associationTemp,
        $associationJson,
        [System.Text.UTF8Encoding]::new($false))
    Set-AssociationFileAcl $associationTemp
    if (Test-Path -LiteralPath $associationPath) {
        $existingAssociation = Get-Item -LiteralPath $associationPath -Force
        if (($existingAssociation.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'association is a reparse point'
        }
        [System.IO.File]::Replace($associationTemp, $associationPath, $null, $true)
    } else {
        Move-Item -LiteralPath $associationTemp -Destination $associationPath
    }
    $associationChanged = $true
    $associationTemp = $null
    Set-AssociationFileAcl $associationPath
    if (-not ([IO.File]::ReadAllText($associationPath)).Equals(
            $associationJson, [StringComparison]::Ordinal)) {
        throw 'association verification failed'
    }

    Start-Service -Name $serviceName
    $service = Get-Service -Name $serviceName
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    $service.Refresh()
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw 'service did not reach Running within 30 seconds'
    }
    $configured = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($null -eq $configured -or
        $configured.PathName -cne $serviceBinaryPath -or
        $configured.StartName -cne 'LocalSystem' -or
        $configured.StartMode -cne 'Auto') {
        throw 'SCM verification failed'
    }

    $committed = $true
    try {
        if (Test-Path -LiteralPath $backupRoot) {
            Assert-NoReparseTree $backupRoot $false
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $associationBackupPath) {
            Assert-NoReparseTree $associationBackupPath $true
            Remove-Item -LiteralPath $associationBackupPath -Force
        }
        Assert-NoReparseTree $serviceDataRoot $false
        foreach ($child in Get-ChildItem -LiteralPath $serviceDataRoot -Force -Directory) {
            if ($child.Name -cne $pipeName -and
                $child.Name -cmatch '^ClashSharp\.Mihomo\.[0-9a-f]{32}$') {
                Assert-NoReparseTree $child.FullName $false
                Remove-Item -LiteralPath $child.FullName -Recurse -Force
            }
        }
    } catch {
        throw [InvalidOperationException]::new(
            'installer.machine.post_commit_cleanup_failed: ' + $_.Exception.Message)
    }
} catch {
    $primaryFailure = $_.Exception.Message
    if (-not $mutationStarted -and
        $primaryFailure.StartsWith('installer.app.running:', [StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine($primaryFailure)
        exit 23
    }
    $rollbackFailure = $null
    if (-not $committed -and -not $rollForwardOnly) {
        try {
            if ($mutationStarted) {
                Stop-ServiceAndWait

                if ($payloadSwapped -and (Test-Path -LiteralPath $currentRoot)) {
                    Assert-NoReparseTree $currentRoot $false
                    Remove-Item -LiteralPath $currentRoot -Recurse -Force
                }
                if ($oldCurrentBackedUp -and (Test-Path -LiteralPath $backupRoot)) {
                    Assert-NoReparseTree $backupRoot $false
                    if (Test-Path -LiteralPath $currentRoot) {
                        Assert-NoReparseTree $currentRoot $false
                        Remove-Item -LiteralPath $currentRoot -Recurse -Force
                    }
                    Copy-Item -LiteralPath $backupRoot -Destination $currentRoot -Recurse
                    Assert-NoReparseTree $currentRoot $false
                }

                if ($oldAssociationExisted) {
                    $rollbackAssociation = Join-Path $serviceDataRoot ".association-rollback-$nonce.tmp"
                    [IO.File]::WriteAllBytes($rollbackAssociation, $oldAssociationBytes)
                    if (Test-Path -LiteralPath $associationPath) {
                        $associationItem = Get-Item -LiteralPath $associationPath -Force
                        if (($associationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw 'rollback association target is a reparse point'
                        }
                        [IO.File]::Replace($rollbackAssociation, $associationPath, $null, $true)
                    } else {
                        Move-Item -LiteralPath $rollbackAssociation -Destination $associationPath
                    }
                    Set-Acl -LiteralPath $associationPath -AclObject $oldAssociationAcl
                } elseif (Test-Path -LiteralPath $associationPath) {
                    $associationItem = Get-Item -LiteralPath $associationPath -Force
                    if (($associationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw 'rollback association target is a reparse point'
                    }
                    Remove-Item -LiteralPath $associationPath -Force
                }

                if ($oldServiceExisted) {
                    $oldStartArgument = switch ([string]$oldServiceCim.StartMode) {
                        'Auto' { 'auto' }
                        'Manual' { 'demand' }
                        'Disabled' { 'disabled' }
                        default { throw 'old service start mode is unsupported' }
                    }
                    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($null -eq $service) {
                        Invoke-Sc -Arguments @(
                            'create', $serviceName, 'binPath=', [string]$oldServiceCim.PathName,
                            'type=', 'own', 'start=', $oldStartArgument, 'obj=', 'LocalSystem',
                            'DisplayName=', [string]$oldServiceCim.DisplayName)
                    } else {
                        Invoke-Sc -Arguments @(
                            'config', $serviceName, 'binPath=', [string]$oldServiceCim.PathName,
                            'type=', 'own', 'start=', $oldStartArgument, 'obj=', 'LocalSystem',
                            'DisplayName=', [string]$oldServiceCim.DisplayName)
                    }
                    if ($oldServiceDelayed) {
                        Invoke-Sc -Arguments @('config', $serviceName, 'start=', 'delayed-auto')
                    }
                    Invoke-Sc -Arguments @(
                        'description', $serviceName, [string]$oldServiceCim.Description)
                    Invoke-Sc -Arguments @('sdset', $serviceName, $oldServiceSddl)
                    if ($oldServiceWasRunning) {
                        Start-Service -Name $serviceName
                        $service = Get-Service -Name $serviceName
                        $service.WaitForStatus(
                            [System.ServiceProcess.ServiceControllerStatus]::Running,
                            [TimeSpan]::FromSeconds(30))
                        $service.Refresh()
                        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                            throw 'old service did not return to Running during rollback'
                        }
                    }
                } else {
                    Remove-ServiceAndWait
                }
            }

            foreach ($temporaryPath in @($stageRoot, $packageStage, $associationTemp)) {
                if ([string]::IsNullOrWhiteSpace([string]$temporaryPath) -or
                    -not (Test-Path -LiteralPath $temporaryPath)) { continue }
                $temporaryItem = Get-Item -LiteralPath $temporaryPath -Force
                if (($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "rollback temporary path is a reparse point: $temporaryPath"
                }
                Remove-Item -LiteralPath $temporaryPath -Recurse -Force
            }

            if ($rootsPrepared) {
                Restore-DirectoryState $serviceDataRoot $serviceDataExisted $serviceDataAclBefore
                Restore-DirectoryState $productDataRoot $productDataExisted $productDataAclBefore
                Restore-DirectoryState $machineRoot $machineRootExisted $machineRootAclBefore
                Restore-DirectoryState $productFilesRoot $productFilesExisted $productFilesAclBefore
            }
        } catch {
            $rollbackFailure = $_.Exception.Message
        }
    }
    if ($null -ne $rollbackFailure) {
        if ($primaryFailure.StartsWith(
                'installer.machine.service_delete_pending_reboot',
                [StringComparison]::Ordinal) -or
            $rollbackFailure.StartsWith(
                'installer.machine.service_delete_pending_reboot',
                [StringComparison]::Ordinal)) {
            [Console]::Error.WriteLine(
                "installer.machine.service_delete_pending_reboot: primary=$primaryFailure; rollback=$rollbackFailure; backup=$backupRoot")
            exit 25
        }
        [Console]::Error.WriteLine(
            "installer.machine.rollback_failed: primary=$primaryFailure; rollback=$rollbackFailure; backup=$backupRoot")
    } else {
        if ($primaryFailure.StartsWith(
                'installer.machine.service_delete_pending_reboot',
                [StringComparison]::Ordinal)) {
            [Console]::Error.WriteLine($primaryFailure)
            exit 25
        }
        [Console]::Error.WriteLine('installer.machine.apply_failed: ' + $primaryFailure)
    }
    exit 1
}
"#;

const UNINSTALL_SCRIPT_TEMPLATE: &str = r#"
$ErrorActionPreference = 'Stop'
try {
    $serviceName = @@CLASHSHARP_SERVICE_NAME@@
    $targetSid = @@CLASHSHARP_TARGET_SID@@
    $packageIdentityName = @@CLASHSHARP_PACKAGE_IDENTITY_NAME@@
    $expectedFamilyName = @@CLASHSHARP_PACKAGE_FAMILY_NAME@@
    $programFilesRoot = @@CLASHSHARP_PROGRAM_FILES_ROOT@@
    $programDataRoot = @@CLASHSHARP_PROGRAM_DATA_ROOT@@
    $machineRoot = @@CLASHSHARP_MACHINE_ROOT@@
    $serviceDataRoot = @@CLASHSHARP_SERVICE_DATA_ROOT@@
    $associationPath = @@CLASHSHARP_ASSOCIATION_PATH@@
    $transactionPath = @@CLASHSHARP_TRANSACTION_PATH@@
    $expectedTransactionId = @@CLASHSHARP_TRANSACTION_ID@@
    $expectedPackageVersion = @@CLASHSHARP_EXPECTED_PACKAGE_VERSION@@
    $expectedPayloadSha256 = @@CLASHSHARP_INSTALLER_PAYLOAD_SHA256@@
    $productFilesRoot = Split-Path -Parent $machineRoot
    $productDataRoot = Split-Path -Parent $serviceDataRoot
    $transactionRoot = Split-Path -Parent $transactionPath
    $scExe = Join-Path ([Environment]::SystemDirectory) 'sc.exe'

    function Assert-TargetPackageQuiescent(
        [string] $InstallRoot,
        [string] $OwnerSid) {
        $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([char]92)
        if (-not [IO.Path]::IsPathRooted($root)) {
            throw 'registered package process root is invalid'
        }
        $prefix = $root + [IO.Path]::DirectorySeparatorChar
        foreach ($process in @(Get-CimInstance -ClassName Win32_Process)) {
            $candidate = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
            try {
                $candidate = [IO.Path]::GetFullPath($candidate)
            } catch {
                if ($candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "registered package process path is ambiguous: $($process.ProcessId)"
                }
                continue
            }
            if (-not $candidate.StartsWith(
                    $prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            try {
                $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
            } catch {
                $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                    'ProcessId = ' + [uint32]$process.ProcessId))
                if ($remaining.Count -eq 0) { continue }
                throw "registered package process token is unreadable: $($process.ProcessId)"
            }
            if ($null -eq $owner -or [uint32]$owner.ReturnValue -ne 0 -or
                [string]::IsNullOrWhiteSpace([string]$owner.Sid)) {
                $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                    'ProcessId = ' + [uint32]$process.ProcessId))
                if ($remaining.Count -eq 0) { continue }
                throw "registered package process token is ambiguous: $($process.ProcessId)"
            }
            if ([string]$owner.Sid -ceq $OwnerSid) {
                throw "installer.app.running: pid=$($process.ProcessId)"
            }
        }
    }

    function Assert-SafeDirectoryChain([string] $Root, [string] $Target) {
        $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $targetFull = [IO.Path]::GetFullPath($Target).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
        if (-not $targetFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -and
            -not $targetFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "fixed path escaped trusted root: $Target"
        }
        $rootItem = Get-Item -LiteralPath $rootFull -Force
        if (-not $rootItem.PSIsContainer -or
            ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "trusted root is unsafe: $rootFull"
        }
        if ($targetFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return }
        $relative = $targetFull.Substring($prefix.Length)
        $current = $rootFull
        foreach ($segment in $relative.Split(
            [char[]]@([IO.Path]::DirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = Join-Path $current $segment
            if (-not (Test-Path -LiteralPath $current)) { continue }
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "unsafe directory ancestor: $current"
            }
        }
    }

    function Assert-NoReparseTree([string] $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "reparse point rejected: $Path"
        }
        if (-not $item.PSIsContainer) { throw "expected directory: $Path" }
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "reparse point rejected: $($child.FullName)"
            }
        }
    }

    Assert-SafeDirectoryChain $programFilesRoot $machineRoot
    Assert-SafeDirectoryChain $programDataRoot $serviceDataRoot
    Assert-SafeDirectoryChain $programDataRoot $transactionRoot

    if (-not (Test-Path -LiteralPath $transactionPath -PathType Leaf)) {
        throw 'protected uninstall transaction is missing'
    }
    $transactionItem = Get-Item -LiteralPath $transactionPath -Force
    if (($transactionItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $transactionItem.Length -lt 1 -or $transactionItem.Length -gt 4096) {
        throw 'protected uninstall transaction is unsafe'
    }
    $transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
    $transactionProperties = @($transaction.PSObject.Properties.Name)
    $requiredTransactionProperties = @(
        'schema', 'transactionId', 'operation', 'targetSid', 'allowReassociation',
        'expectedPackageVersion', 'installerPayloadSha256', 'phase', 'generation')
    $versionParts = @([string]$transaction.expectedPackageVersion -split '\.')
    $canonicalVersion = $versionParts.Count -eq 4 -and @($versionParts | Where-Object {
        $_ -cnotmatch '^(0|[1-9][0-9]{0,4})$' -or [uint32]$_ -gt 65535
    }).Count -eq 0
    $canonicalPhaseGeneration =
        ([string]$transaction.phase -ceq 'prepared' -and [uint32]$transaction.generation -eq 1) -or
        ([string]$transaction.phase -ceq 'machineCommitted' -and [uint32]$transaction.generation -eq 2) -or
        ([string]$transaction.phase -ceq 'packageCommitted' -and [uint32]$transaction.generation -eq 3) -or
        ([string]$transaction.phase -ceq 'verified' -and [uint32]$transaction.generation -eq 4)
    if ($transactionProperties.Count -ne $requiredTransactionProperties.Count -or
        @($requiredTransactionProperties | Where-Object {
            $transactionProperties -cnotcontains $_
        }).Count -ne 0 -or
        [uint32]$transaction.schema -ne 2 -or
        [string]$transaction.transactionId -cne $expectedTransactionId -or
        [string]$transaction.transactionId -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$transaction.operation -cne 'uninstall' -or
        [string]$transaction.targetSid -cne $targetSid -or
        $transaction.allowReassociation -isnot [bool] -or
        [bool]$transaction.allowReassociation -ne $false -or
        -not $canonicalVersion -or
        [string]$transaction.expectedPackageVersion -cne $expectedPackageVersion -or
        [string]$transaction.installerPayloadSha256 -cne $expectedPayloadSha256 -or
        [string]$transaction.installerPayloadSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not $canonicalPhaseGeneration) {
        throw 'protected uninstall transaction does not authorize this mutation'
    }

    if (Test-Path -LiteralPath $associationPath) {
        $associationItem = Get-Item -LiteralPath $associationPath -Force
        if (-not $associationItem.PSIsContainer -and
            ($associationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $associationItem.Length -ge 1 -and $associationItem.Length -le 4096) {
            $association = Get-Content -LiteralPath $associationPath -Raw | ConvertFrom-Json
            $associationProperties = @($association.PSObject.Properties.Name)
            if ($associationProperties.Count -ne 3 -or
                $associationProperties -cnotcontains 'schemaVersion' -or
                $associationProperties -cnotcontains 'ownerSid' -or
                $associationProperties -cnotcontains 'authenticationToken' -or
                $association.schemaVersion -ne 1 -or
                [string]$association.ownerSid -cne $targetSid -or
                [string]$association.authenticationToken -cnotmatch '^[0-9a-f]{64}$') {
                throw 'existing owner association conflicts with uninstall transaction'
            }
        } else {
            throw 'owner association is unsafe'
        }
    }

    $packages = @(Get-AppxPackage -User $targetSid -Name $packageIdentityName)
    if ($packages.Count -gt 1) { throw 'target package registration is ambiguous' }
    if ($packages.Count -eq 1) {
        $package = $packages[0]
        $packageFullName = [string]$package.PackageFullName
        $installRoot = [IO.Path]::GetFullPath(
            [string]$package.InstallLocation).TrimEnd([char]92)
        if ([string]::IsNullOrWhiteSpace($packageFullName) -or
            [string]::IsNullOrWhiteSpace($installRoot) -or
            [string]$package.Name -cne $packageIdentityName -or
            [string]$package.PackageFamilyName -cne $expectedFamilyName -or
            -not [IO.Path]::IsPathRooted($installRoot) -or
            -not ([IO.Path]::GetFileName($installRoot)).Equals(
                $packageFullName, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'target package registration is not exact'
        }
        Assert-TargetPackageQuiescent $installRoot $targetSid
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }
        & $scExe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0 -and
            $LASTEXITCODE -ne 1060 -and
            $LASTEXITCODE -ne 1072) {
            throw "sc.exe delete failed with exit code $LASTEXITCODE"
        }
        $service.Dispose()
        $service = $null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        & $scExe query $serviceName | Out-Null
        if ($LASTEXITCODE -eq 1060) { break }
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1072) {
            throw "sc.exe query failed with exit code $LASTEXITCODE"
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw 'installer.machine.service_delete_pending_reboot'
        }
        Start-Sleep -Milliseconds 100
    }

    if (Test-Path -LiteralPath $machineRoot) {
        Assert-NoReparseTree $machineRoot
        Remove-Item -LiteralPath $machineRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $serviceDataRoot) {
        Assert-NoReparseTree $serviceDataRoot
        Remove-Item -LiteralPath $serviceDataRoot -Recurse -Force
    }
    foreach ($productRoot in @($productFilesRoot, $productDataRoot)) {
        if (-not (Test-Path -LiteralPath $productRoot -PathType Container)) { continue }
        $productItem = Get-Item -LiteralPath $productRoot -Force
        if (($productItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "product root is a reparse point: $productRoot"
        }
        if (@(Get-ChildItem -LiteralPath $productRoot -Force).Count -eq 0) {
            Remove-Item -LiteralPath $productRoot -Force
        }
    }
} catch {
    if ($_.Exception.Message.StartsWith(
            'installer.app.running:', [StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 23
    }
    if ($_.Exception.Message.StartsWith(
            'installer.machine.service_delete_pending_reboot',
            [StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 25
    }
    [Console]::Error.WriteLine('installer.machine.uninstall_failed: ' + $_.Exception.Message)
    exit 1
}
"#;

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(windows)]
    use crate::process_runner::{ProcessRunOptions, run_bounded_process};
    #[cfg(windows)]
    use std::process::Command;
    #[cfg(windows)]
    use std::time::Duration;

    const SID: &str = "S-1-5-21-100-200-300-1001";
    const OTHER_SID: &str = "S-1-5-21-100-200-300-1002";
    const TOKEN: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const FRESH_TOKEN: &str = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    const VERSION: &str = "1.2.3.4";
    const TEST_MANIFEST: &str = r#"[
        {"path":"binaries/geodata/manifest.json","length":1,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},
        {"path":"binaries/mihomo.exe","length":1,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},
        {"path":"binaries/service/clashsharp.mihomoservice.exe","length":1,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
    ]"#;

    fn registration() -> TargetPackageRegistration {
        let package_full_name =
            format!("{PACKAGE_IDENTITY_NAME}_1.0.0.0_x64__{PACKAGE_PUBLISHER_ID}");
        TargetPackageRegistration {
            install_location: PathBuf::from(r"C:\Program Files\WindowsApps")
                .join(&package_full_name),
            package_full_name,
            package_family_name: String::from(PACKAGE_FAMILY_NAME),
            publisher: String::from(PACKAGE_PUBLISHER),
            publisher_id: String::from(PACKAGE_PUBLISHER_ID),
            signature_kind: String::from("Developer"),
            is_development_mode: false,
            profile_path: PathBuf::from(r"C:\Users\owner"),
        }
    }

    fn service_plan() -> MachineServicePlan {
        MachineServicePlan::new(
            &registration(),
            Path::new(r"C:\Program Files"),
            Path::new(r"C:\ProgramData"),
            MachineTransactionContext::new(TOKEN, SID, None, TOKEN),
            MachinePayloadSource::new(
                Path::new(r"C:\release\payload\ClashSharp.msix"),
                TOKEN,
                TEST_MANIFEST,
            ),
        )
        .unwrap()
    }

    #[cfg(windows)]
    fn assert_powershell_parses(script: &str) {
        let mut command = Command::new("powershell.exe");
        command.args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            "$source=[Console]::In.ReadToEnd(); $tokens=$null; $errors=$null; \
             [Management.Automation.Language.Parser]::ParseInput(\
                $source,[ref]$tokens,[ref]$errors) | Out-Null; \
             if ($errors.Count -ne 0) { \
                $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 \
             }",
        ]);
        let output = run_bounded_process(
            &mut command,
            Some(script.as_bytes()),
            ProcessRunOptions::new(Duration::from_secs(15), 64 * 1024, 64 * 1024),
        )
        .unwrap();
        assert!(
            output.status.success(),
            "PowerShell syntax failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }

    #[test]
    fn target_registration_exposes_and_matches_its_strict_package_version() {
        let registration = registration();

        assert_eq!(
            registration.package_full_name(),
            format!("{PACKAGE_IDENTITY_NAME}_1.0.0.0_x64__{PACKAGE_PUBLISHER_ID}")
        );
        assert_eq!(registration.package_version().unwrap(), "1.0.0.0");
        assert!(
            registration
                .matches_trusted_package_version("1.0.0.0")
                .unwrap()
        );
        assert!(
            !registration
                .matches_trusted_package_version("1.0.0.1")
                .unwrap()
        );
        assert!(
            registration
                .matches_trusted_package_version("01.0.0.0")
                .is_err()
        );
    }

    #[test]
    fn target_registration_rejects_ambiguous_package_full_name_versions() {
        for suffix in [
            "1.0.0_x64_",
            "01.0.0.0_x64_",
            "1.0.0.65536_x64_",
            "1.0.0.0_arm64_",
            "1.0.0.0_x64_resource",
        ] {
            let mut registration = registration();
            registration.package_full_name =
                format!("{PACKAGE_IDENTITY_NAME}_{suffix}_{PACKAGE_PUBLISHER_ID}");
            registration.install_location = PathBuf::from(r"C:\Program Files\WindowsApps")
                .join(&registration.package_full_name);

            assert!(registration.validate().is_err(), "accepted {suffix}");
            assert!(registration.package_version().is_err(), "parsed {suffix}");
        }
    }

    #[test]
    fn operation_plan_prepares_before_certificate_and_msix_then_commits_machine() {
        assert_eq!(
            operation_steps(OperationAction::Repair),
            [
                OperationStep::PrepareMachineRepair,
                OperationStep::InstallCurrentUserCertificate,
                OperationStep::DeployCurrentUserPackageInPlace,
                OperationStep::CommitMachineTransaction,
            ]
        );
        assert_eq!(
            operation_steps(OperationAction::Uninstall),
            [
                OperationStep::RemoveMachineResourcesIfOwner,
                OperationStep::RemoveCurrentUserStartupFallback,
                OperationStep::RemoveCurrentUserPackageIfPresent,
                OperationStep::FinalizeUninstallTransaction,
            ]
        );
        assert!(
            !operation_steps(OperationAction::Repair)
                .contains(&OperationStep::RemoveCurrentUserPackageIfPresent)
        );
    }

    #[test]
    fn interactive_installer_does_not_request_process_wide_elevation() {
        let build_script = include_str!("../build.rs");

        assert!(!build_script.contains("requireAdministrator"));
        assert!(!build_script.contains("requestedExecutionLevel"));
    }

    #[test]
    fn helper_parser_accepts_only_fixed_parameter_shapes() {
        let arguments = vec![
            String::from("--machine-prepare-install"),
            String::from("--target-sid"),
            String::from(SID),
        ];
        assert!(matches!(
            MachineHelperInvocation::parse(&arguments),
            Ok(Some(MachineHelperInvocation::PrepareInstall { .. }))
        ));

        for mode in [
            "--machine-prepare-repair",
            "--machine-commit",
            "--machine-uninstall",
            "--machine-finalize-uninstall",
        ] {
            let fixed = vec![
                String::from(mode),
                String::from("--target-sid"),
                String::from(SID),
            ];
            assert!(MachineHelperInvocation::parse(&fixed).unwrap().is_some());
        }

        let mut arbitrary_path = arguments;
        arbitrary_path.extend([String::from("--source"), String::from(r"C:\attacker")]);
        assert_eq!(
            MachineHelperInvocation::parse(&arbitrary_path),
            Err(String::from("installer.machine_helper.arguments_invalid"))
        );
    }

    #[test]
    fn owner_sid_validation_rejects_noncanonical_or_privileged_values() {
        for invalid in [
            "S-S",
            "S-1-S",
            "S-2-5-21-1",
            "S-01-5-21-1",
            "S-1-05-21-1",
            "S-1-5-021-1",
            "S-1-281474976710656-1",
            "S-1-5-4294967296",
            "S-1-5-18",
            "S-1-5-32-544",
        ] {
            assert!(validate_owner_sid(invalid).is_err(), "accepted {invalid}");
        }
        assert!(validate_owner_sid(SID).is_ok());
        assert!(validate_owner_sid("S-1-12-1-1-2-3-4").is_ok());
    }

    #[test]
    fn association_schema_has_only_owner_and_token() {
        let association = MachineAssociation::new(SID, TOKEN).unwrap();
        let json = association.to_json().unwrap();

        assert_eq!(
            json,
            format!(r#"{{"schemaVersion":1,"ownerSid":"{SID}","authenticationToken":"{TOKEN}"}}"#)
        );
        assert!(MachineAssociation::parse(json.as_bytes()).is_ok());
        assert!(MachineAssociation::parse(
            format!(
                r#"{{"schemaVersion":1,"ownerSid":"{SID}","authenticationToken":"{TOKEN}","pipeName":"x"}}"#
            )
            .as_bytes()
        )
        .is_err());
    }

    #[test]
    fn same_owner_reuses_token_and_nonowner_install_requires_repair() {
        let same_owner = AssociationState::Valid(MachineAssociation::new(SID, TOKEN).unwrap());
        assert_eq!(
            decide_apply_for_owner(SID, false, &same_owner, true, true, FRESH_TOKEN).unwrap(),
            ApplyDecision::Provision {
                authentication_token: String::from(TOKEN)
            }
        );

        let other_owner =
            AssociationState::Valid(MachineAssociation::new(OTHER_SID, TOKEN).unwrap());
        assert_eq!(
            decide_apply_for_owner(SID, false, &other_owner, true, true, FRESH_TOKEN).unwrap(),
            ApplyDecision::RequiresExplicitRepair
        );
        assert_eq!(
            decide_apply_for_owner(SID, true, &other_owner, true, true, FRESH_TOKEN).unwrap(),
            ApplyDecision::Provision {
                authentication_token: String::from(FRESH_TOKEN)
            }
        );
    }

    #[test]
    fn machine_uninstall_is_strictly_owner_checked() {
        let other_owner =
            AssociationState::Valid(MachineAssociation::new(OTHER_SID, TOKEN).unwrap());
        assert!(!may_uninstall_machine(SID, &other_owner).unwrap());
        assert!(!may_uninstall_machine(SID, &AssociationState::Invalid).unwrap());
        assert!(may_uninstall_machine(OTHER_SID, &other_owner).unwrap());
    }

    #[test]
    fn service_plan_uses_stable_machine_payload_and_target_local_state() {
        let plan = service_plan();

        assert_eq!(
            plan.machine_root(),
            Path::new(r"C:\Program Files\ClashSharp\Service")
        );
        assert_eq!(
            plan.config_path(),
            Path::new(
                r"C:\Users\owner\AppData\Local\Packages\67dc1dc3-13fd-46c5-84f4-2932d94b566f_vj7sjtzkt239a\LocalState\mihomo\config.yaml"
            )
        );
        let binary_path = plan.service_binary_path().unwrap();
        assert!(binary_path.starts_with(
            r#""C:\Program Files\ClashSharp\Service\current\Host\ClashSharp.MihomoService.exe""#
        ));
        assert!(!binary_path.contains("WindowsApps"));
    }

    #[test]
    fn apply_script_is_local_fixed_auto_and_fail_closed() {
        let plan = service_plan();
        let script = plan.render_apply_script().unwrap();

        for forbidden in ["Invoke-WebRequest", "curl.exe", "http://", "https://"] {
            assert!(!script.contains(forbidden));
        }
        for required in [
            "Stop-ServiceAndWait",
            "WaitForStatus",
            "Assert-SafeDirectoryChain $programFilesRoot $machineRoot",
            "Assert-SafeDirectoryChain $programDataRoot $serviceDataRoot",
            "Assert-NoReparseTree",
            "$sha.ComputeHash($packageStream)",
            "$packageStream.Position = 0",
            "ZipArchive",
            "Assert-StagedPayload $stageRoot",
            "Move-Item -LiteralPath $stageRoot -Destination $currentRoot",
            "'start=', 'delayed-auto'",
            "Start-Service -Name $serviceName",
            "Set-AssociationFileAcl",
            "File]::Replace",
            "CCLCSWLOCRRC",
            "oldServiceSddl",
            "$oldServiceCim.ServiceType -cne 'Own Process'",
            "Assert-TargetPackageQuiescent $registeredRoot $targetSidText",
            "oldAssociationBytes",
            "productDataAclBefore",
            "serviceDataAclBefore",
            "Remove-ServiceAndWait",
            "installer.machine.rollback_failed",
            "$child.Name -cne $pipeName",
            "$rollForwardOnly = $true",
            "'config', $serviceName, 'start=', 'disabled'",
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)",
            "if (-not $committed -and -not $rollForwardOnly)",
            "installer.machine.post_commit_cleanup_failed",
        ] {
            assert!(
                script.contains(required),
                "missing script contract: {required}"
            );
        }
        assert!(!script.contains("$env:SystemRoot"));
        let start = script.find("Start-Service -Name $serviceName").unwrap();
        let quiescent = script
            .find("Assert-TargetPackageQuiescent $registeredRoot $targetSidText")
            .unwrap();
        let mutation = script.find("$mutationStarted = $true").unwrap();
        let stop = script[mutation..].find("Stop-ServiceAndWait").unwrap() + mutation;
        let disable = script[stop..]
            .find("'config', $serviceName, 'start=', 'disabled'")
            .unwrap()
            + stop;
        let fence = script[disable..]
            .find("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)")
            .unwrap()
            + disable;
        let swap = script[fence..]
            .find("Move-Item -LiteralPath $stageRoot -Destination $currentRoot")
            .unwrap()
            + fence;
        let cleanup = script.find("$child.Name -cne $pipeName").unwrap();
        assert!(quiescent < mutation);
        assert!(mutation < stop && stop < disable && disable < fence && fence < swap);
        assert!(script.contains(&format!("$nonce = '{TOKEN}'")));
        assert!(
            start < cleanup,
            "old endpoints may only be cleaned after commit"
        );
    }

    #[cfg(windows)]
    #[test]
    fn embedded_machine_powershell_scripts_have_valid_syntax() {
        let plan = service_plan();
        let resources =
            MachineResourcePlan::new(Path::new(r"C:\Program Files"), Path::new(r"C:\ProgramData"))
                .unwrap();

        assert_powershell_parses(&plan.render_apply_script().unwrap());
        assert_powershell_parses(
            &resources
                .render_uninstall_script(
                    SID,
                    Path::new(r"C:\ProgramData\ClashSharp\Installer\transaction.json"),
                    TOKEN,
                    VERSION,
                    FRESH_TOKEN,
                )
                .unwrap(),
        );
    }

    #[test]
    fn uninstall_script_rechecks_protected_transaction_owner_and_fixed_roots() {
        let resources =
            MachineResourcePlan::new(Path::new(r"C:\Program Files"), Path::new(r"C:\ProgramData"))
                .unwrap();
        let script = resources
            .render_uninstall_script(
                SID,
                Path::new(r"C:\ProgramData\ClashSharp\Installer\transaction.json"),
                TOKEN,
                VERSION,
                FRESH_TOKEN,
            )
            .unwrap();

        assert!(script.contains("ownerSid"));
        assert!(script.contains(SID));
        assert!(script.contains("protected uninstall transaction"));
        assert!(script.contains(r"C:\ProgramData\ClashSharp\Installer\transaction.json"));
        assert!(script.contains("'operation', 'targetSid'"));
        assert!(script.contains("'machineCommitted'"));
        assert!(!script.contains("owner association is missing"));
        assert!(script.contains(r"C:\Program Files\ClashSharp\Service"));
        assert!(script.contains(r"C:\ProgramData\ClashSharp\MihomoService"));
        assert!(script.contains("Assert-NoReparseTree"));
        assert!(script.contains("Assert-SafeDirectoryChain $programFilesRoot $machineRoot"));
        assert!(script.contains("Assert-SafeDirectoryChain $programDataRoot $serviceDataRoot"));
        assert!(script.contains("[Environment]::SystemDirectory"));
        assert!(!script.contains("$env:SystemRoot"));
        assert!(script.contains("Get-AppxPackage -User $targetSid -Name $packageIdentityName"));
        assert!(script.contains(PACKAGE_IDENTITY_NAME));
        assert!(script.contains(PACKAGE_FAMILY_NAME));
        let quiescent = script
            .find("Assert-TargetPackageQuiescent $installRoot $targetSid")
            .unwrap();
        let service = script
            .find("$service = Get-Service -Name $serviceName")
            .unwrap();
        assert!(quiescent < service);
    }

    #[test]
    fn pipe_derivation_matches_protocol_vector() {
        assert_eq!(
            build_pipe_name(SID, TOKEN).unwrap(),
            "ClashSharp.Mihomo.889ca1a80c0bd15fb9c7cc8c51e2753d"
        );
    }

    #[test]
    fn random_token_is_canonical_and_not_constant() {
        let first = generate_token().unwrap();
        let second = generate_token().unwrap();
        assert!(validate_token(&first).is_ok());
        assert!(validate_token(&second).is_ok());
        assert_ne!(first, second);
    }
}
