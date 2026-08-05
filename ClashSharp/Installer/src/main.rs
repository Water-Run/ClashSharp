#![cfg_attr(windows, windows_subsystem = "windows")]

use std::cell::RefCell;
use std::fs::{File, OpenOptions};
use std::io::Write as _;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;

use clashsharp_installer::installer_transaction::{
    INSTALLER_TRANSACTION_RELATIVE_PATH, InstallerTransactionJournal, InstallerTransactionPhase,
    MAX_INSTALLER_TRANSACTION_BYTES,
};
use clashsharp_installer::metadata::{
    compact_path, parse_version_from_package_name, read_manifest_version_text,
};
use clashsharp_installer::service_plan::{
    ApplyDecision, AssociationState, MachineAssociation, MachineHelperInvocation,
    MachinePayloadSource, MachineResourcePlan, MachineServicePlan, MachineTransactionContext,
    OperationAction, OperationStep, PACKAGE_FAMILY_NAME, PACKAGE_IDENTITY_NAME, SERVICE_NAME,
    TargetPackageRegistration, decide_apply_for_owner, generate_token, may_uninstall_machine,
    operation_steps, validate_owner_sid,
};
use clashsharp_installer::trust_anchor::{
    trusted_machine_manifest_json, trusted_msix_sha256, trusted_package_version,
    verify_installer_payload, verify_registered_machine_payload,
};
use slint::{ComponentHandle, SharedString, Weak};

#[cfg(windows)]
use std::os::windows::ffi::OsStrExt;
#[cfg(windows)]
use std::os::windows::fs::MetadataExt;
#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;
#[cfg(windows)]
use std::os::windows::io::AsRawHandle;
#[cfg(windows)]
use std::os::windows::process::CommandExt;

slint::include_modules!();

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

const GITHUB_URL: &str = "https://github.com/Water-Run/ClashSharp";
const CLASHSHARP_LICENSE: &str = "AGPL-3.0";
const MACHINE_HELPER_FAILURE_EXIT_CODE: i32 = 1;
const MACHINE_HELPER_REPAIR_REQUIRED_EXIT_CODE: i32 = 20;
const APP_RUNNING_EXIT_CODE: i32 = 23;
const MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE: i32 = 25;
const MACHINE_TRANSACTION_CONFLICT_EXIT_CODE: i32 = 26;
const MACHINE_TRANSACTION_PACKAGE_NOT_COMMITTED_EXIT_CODE: i32 = 28;
const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;
const IMAGE_FILE_MACHINE_ARM64: u16 = 0xaa64;
const MACHINE_MUTATION_MUTEX_NAME: &str = r"Global\ClashSharp.InstallerMachineMutation.v1";
const MACHINE_MUTATION_MUTEX_WAIT_MS: u32 = 30_000;
#[cfg(windows)]
const FILE_SHARE_READ: u32 = 0x0000_0001;
#[cfg(windows)]
const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
static ACTION_RUNNING: AtomicBool = AtomicBool::new(false);

/// Strict result of reading the fixed protected Installer transaction journal.
#[derive(Clone, Debug, Eq, PartialEq)]
enum InstallerTransactionState {
    Missing,
    Valid(InstallerTransactionJournal),
    Invalid,
}

/// User-requested package operation.
#[derive(Clone, Copy)]
enum InstallerAction {
    /// Install the certificate and deploy the MSIX package.
    Install,
    /// Reinstall the certificate and redeploy the package.
    Repair,
    /// Remove the current user's installed MSIX package.
    Uninstall,
}

/// Coarse installer phase mirrored into the Slint UI.
#[derive(Clone, Copy)]
enum InstallerPhase {
    /// Device and payload checks are running.
    Checking,
    /// The current Windows environment is unsupported.
    Blocked,
    /// The installer is ready for a user action.
    Ready,
    /// An install, repair, or uninstall action is running.
    Working,
    /// The last operation completed.
    Completed,
    /// The last check or operation failed.
    Failed,
}

/// Display language options aligned with the Clash# main application.
#[derive(Clone, Copy)]
enum InstallerLanguage {
    /// Resolve from the operating system UI culture.
    AutoDetect,
    /// Simplified Chinese (`zh-Hans`).
    SimplifiedChinese,
    /// Traditional Chinese (`zh-Hant`).
    TraditionalChinese,
    /// English (`en-US`).
    English,
    /// Russian (`ru`).
    Russian,
    /// French (`fr`).
    French,
    /// German (`de`).
    German,
}

impl InstallerLanguage {
    /// Converts the Slint combobox index into a language value.
    fn from_index(index: i32) -> Self {
        match index {
            1 => Self::SimplifiedChinese,
            2 => Self::TraditionalChinese,
            3 => Self::English,
            4 => Self::Russian,
            5 => Self::French,
            6 => Self::German,
            _ => Self::AutoDetect,
        }
    }

    /// Returns the Slint combobox index for this language.
    fn index(self) -> i32 {
        match self {
            Self::AutoDetect => 0,
            Self::SimplifiedChinese => 1,
            Self::TraditionalChinese => 2,
            Self::English => 3,
            Self::Russian => 4,
            Self::French => 5,
            Self::German => 6,
        }
    }
}

/// Installer appearance preference.
#[derive(Clone, Copy)]
enum ThemeMode {
    /// Follow the Windows app theme setting.
    Auto,
    /// Force light appearance.
    Light,
    /// Force dark appearance.
    Dark,
}

impl ThemeMode {
    /// Converts the Slint selector index into a theme mode.
    fn from_index(index: i32) -> Self {
        match index {
            1 => Self::Light,
            2 => Self::Dark,
            _ => Self::Auto,
        }
    }

    /// Returns the Slint selector index for this theme mode.
    fn index(self) -> i32 {
        match self {
            Self::Auto => 0,
            Self::Light => 1,
            Self::Dark => 2,
        }
    }
}

/// Mutable user preferences kept while the installer process is open.
#[derive(Clone, Copy)]
struct AppPreferences {
    language: InstallerLanguage,
    theme_mode: ThemeMode,
}

/// Localized strings consumed by the Slint UI and action progress messages.
#[derive(Clone, Copy)]
struct TextPack {
    window_title: &'static str,
    product_title: &'static str,
    product_subtitle: &'static str,
    checking_title: &'static str,
    checking_message: &'static str,
    unsupported_title: &'static str,
    unsupported_message: &'static str,
    missing_payload_title: &'static str,
    missing_payload_message: &'static str,
    not_installed_title: &'static str,
    not_installed_message: &'static str,
    installed_title: &'static str,
    installed_message: &'static str,
    install_button: &'static str,
    repair_button: &'static str,
    uninstall_button: &'static str,
    refresh_button: &'static str,
    admin_hint: &'static str,
    preparing_install: &'static str,
    preparing_repair: &'static str,
    preparing_uninstall: &'static str,
    certificate_title: &'static str,
    certificate_message: &'static str,
    removing_title: &'static str,
    removing_message: &'static str,
    package_title: &'static str,
    package_message: &'static str,
    uninstall_title: &'static str,
    uninstall_message: &'static str,
    installed_done: &'static str,
    repaired_done: &'static str,
    uninstalled_done: &'static str,
    failed_title: &'static str,
    details_title: &'static str,
    close_button: &'static str,
    language_auto: &'static str,
    theme_follow_system: &'static str,
    theme_light: &'static str,
    theme_dark: &'static str,
    system_supported: &'static str,
    system_unsupported: &'static str,
    installed_yes: &'static str,
    installed_no: &'static str,
    package_missing: &'static str,
    certificate_missing: &'static str,
    dependencies_none: &'static str,
    dependencies_prefix: &'static str,
    version_label: &'static str,
    license_label: &'static str,
}

/// Paths and current installation state discovered from the installer payload.
struct InstallerContext {
    payload_dir: PathBuf,
    package_path: Option<PathBuf>,
    certificate_path: Option<PathBuf>,
    dependency_paths: Vec<PathBuf>,
    is_installed: bool,
}

/// Per-user startup barriers held by the non-elevated parent for one whole package operation.
struct PackageMutationLocks {
    _operation_lock: File,
    _installer_mutation_reader: File,
    installer_mutation_path: PathBuf,
    recovery_lock: Option<File>,
}

impl PackageMutationLocks {
    fn installer_mutation_path(&self) -> &Path {
        &self.installer_mutation_path
    }

    fn acquire_recovery_lock(&mut self, target_sid: &str) -> Result<(), String> {
        if self.recovery_lock.is_some() {
            return Ok(());
        }

        ensure_current_user_package_stopped(target_sid)?;
        let lock = open_exclusive_coordination_lock(
            &query_current_user_recovery_lock_path()?,
            "installer.app.running: Close Clash# completely (including its recovery helper), then retry.",
        )?;
        ensure_current_user_package_stopped(target_sid)?;
        self.recovery_lock = Some(lock);
        Ok(())
    }

    fn release_recovery_lock(&mut self) {
        self.recovery_lock = None;
    }
}

/// Combined result of environment, payload, and installed-package inspection.
struct EnvironmentState {
    context: Result<InstallerContext, String>,
    support: Result<SystemInfo, String>,
    is_installed: bool,
}

/// Windows platform facts needed before MSIX deployment.
struct SystemInfo {
    build: u32,
    architecture: String,
}

/// Creates the UI, wires callbacks, starts the initial environment check, and runs the Slint event loop.
fn main() -> Result<(), slint::PlatformError> {
    let helper_arguments = std::env::args().skip(1).collect::<Vec<_>>();
    match MachineHelperInvocation::parse(&helper_arguments) {
        Ok(Some(invocation)) => std::process::exit(run_machine_helper(invocation)),
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(MACHINE_HELPER_FAILURE_EXIT_CODE);
        }
        Ok(None) => {}
    }

    let preferences = Rc::new(RefCell::new(AppPreferences {
        language: detect_system_language(),
        theme_mode: ThemeMode::Auto,
    }));

    let app = MainWindow::new()?;
    let initial_preferences = *preferences.borrow();
    apply_text(
        &app,
        localized_text(resolve_language(initial_preferences.language)),
    );
    apply_language(&app, initial_preferences.language);
    apply_theme(&app, initial_preferences.theme_mode);
    app.set_show_details(false);
    app.set_show_theme_menu(false);
    app.set_show_language_menu(false);

    let app_weak = app.as_weak();
    begin_refresh(
        app_weak.clone(),
        localized_text(resolve_language(initial_preferences.language)),
    );

    app.on_set_language({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move |index| {
            let language = InstallerLanguage::from_index(index);
            preferences.borrow_mut().language = language;
            let resolved_language = resolve_language(language);

            if let Some(handle) = app_weak.upgrade() {
                apply_language(&handle, language);
                apply_text(&handle, localized_text(resolved_language));
            }

            begin_refresh(app_weak.clone(), localized_text(resolved_language));
        }
    });

    app.on_set_theme({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move |index| {
            let mode = ThemeMode::from_index(index);
            preferences.borrow_mut().theme_mode = mode;

            if let Some(handle) = app_weak.upgrade() {
                apply_theme(&handle, mode);
            }
        }
    });

    app.on_open_details({
        let app_weak = app_weak.clone();
        move || {
            if let Some(handle) = app_weak.upgrade() {
                handle.set_show_details(true);
            }
        }
    });

    app.on_hide_details({
        let app_weak = app_weak.clone();
        move || {
            if let Some(handle) = app_weak.upgrade() {
                handle.set_show_details(false);
            }
        }
    });

    app.on_open_github(|| {
        let _ = hidden_command("powershell.exe")
            .args([
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                &format!("Start-Process {}", powershell_quote_text(GITHUB_URL)),
            ])
            .spawn();
    });

    app.on_refresh_state({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move || {
            let text = localized_text(resolve_language(preferences.borrow().language));
            begin_refresh(app_weak.clone(), text);
        }
    });

    app.on_install({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move || {
            let text = localized_text(resolve_language(preferences.borrow().language));
            run_action_async(app_weak.clone(), InstallerAction::Install, text);
        }
    });

    app.on_repair({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move || {
            let text = localized_text(resolve_language(preferences.borrow().language));
            run_action_async(app_weak.clone(), InstallerAction::Repair, text);
        }
    });

    app.on_uninstall({
        let preferences = Rc::clone(&preferences);
        let app_weak = app_weak.clone();
        move || {
            let text = localized_text(resolve_language(preferences.borrow().language));
            run_action_async(app_weak.clone(), InstallerAction::Uninstall, text);
        }
    });

    app.run()
}

/// Applies the selected language index and compact button label.
fn apply_language(handle: &MainWindow, language: InstallerLanguage) {
    handle.set_language_index(language.index());
    handle.set_language_short_text(SharedString::from(language_short_label(language)));
}

/// Applies the selected theme mode and resolves automatic mode against Windows settings.
fn apply_theme(handle: &MainWindow, mode: ThemeMode) {
    handle.set_theme_index(mode.index());
    handle.set_effective_theme(match mode {
        ThemeMode::Auto => {
            if detect_system_dark_theme() {
                1
            } else {
                0
            }
        }
        ThemeMode::Light => 0,
        ThemeMode::Dark => 1,
    });
}

/// Copies a localized text pack into Slint properties.
fn apply_text(handle: &MainWindow, text: TextPack) {
    handle.set_window_title(SharedString::from(text.window_title));
    handle.set_product_title(SharedString::from(text.product_title));
    handle.set_product_subtitle(SharedString::from(text.product_subtitle));
    handle.set_install_text(SharedString::from(text.install_button));
    handle.set_repair_text(SharedString::from(text.repair_button));
    handle.set_uninstall_text(SharedString::from(text.uninstall_button));
    handle.set_refresh_text(SharedString::from(text.refresh_button));
    handle.set_admin_hint_text(SharedString::from(text.admin_hint));
    handle.set_details_title(SharedString::from(text.details_title));
    handle.set_close_text(SharedString::from(text.close_button));
    handle.set_language_auto_text(SharedString::from(text.language_auto));
    handle.set_theme_follow_system_text(SharedString::from(text.theme_follow_system));
    handle.set_theme_light_text(SharedString::from(text.theme_light));
    handle.set_theme_dark_text(SharedString::from(text.theme_dark));
}

/// Starts asynchronous environment inspection and resets the UI to checking state.
fn begin_refresh(app_weak: Weak<MainWindow>, text: TextPack) {
    if let Some(handle) = app_weak.upgrade() {
        handle.set_phase(InstallerPhase::Checking as i32);
        handle.set_busy(true);
        handle.set_progress(0.0);
        handle.set_show_details(false);
        handle.set_state_title(SharedString::from(text.checking_title));
        handle.set_state_message(SharedString::from(text.checking_message));
        handle.set_details_text(SharedString::from(""));
    }

    thread::spawn(move || {
        let state = inspect_environment();
        app_weak
            .upgrade_in_event_loop(move |handle| {
                apply_environment_state(&handle, &state, text);
            })
            .ok();
    });
}

/// Inspects payload files, OS support, and current package installation state.
fn inspect_environment() -> EnvironmentState {
    EnvironmentState {
        context: build_context(),
        support: inspect_supported_system(),
        is_installed: is_package_installed(),
    }
}

/// Projects environment inspection results into the UI state machine.
fn apply_environment_state(handle: &MainWindow, state: &EnvironmentState, text: TextPack) {
    let supported = state.support.is_ok();
    handle.set_busy(false);
    handle.set_supported(supported);
    handle.set_installed(state.is_installed);
    handle.set_progress(0.0);
    handle.set_details_text(SharedString::from(format_environment_details(state, text)));

    if state.support.is_err() {
        handle.set_phase(InstallerPhase::Blocked as i32);
        handle.set_state_title(SharedString::from(text.unsupported_title));
        handle.set_state_message(SharedString::from(text.unsupported_message));
        return;
    }

    let Ok(context) = state.context.as_ref() else {
        handle.set_phase(InstallerPhase::Failed as i32);
        handle.set_state_title(SharedString::from(text.missing_payload_title));
        handle.set_state_message(SharedString::from(text.missing_payload_message));
        return;
    };

    if context.package_path.is_none() || context.certificate_path.is_none() {
        handle.set_phase(InstallerPhase::Failed as i32);
        handle.set_state_title(SharedString::from(text.missing_payload_title));
        handle.set_state_message(SharedString::from(text.missing_payload_message));
        return;
    }

    handle.set_phase(InstallerPhase::Ready as i32);
    handle.set_state_title(SharedString::from(if state.is_installed {
        text.installed_title
    } else {
        text.not_installed_title
    }));
    handle.set_state_message(SharedString::from(if state.is_installed {
        text.installed_message
    } else {
        text.not_installed_message
    }));
}

/// Runs an install action on a worker thread and posts progress back to the UI thread.
fn run_action_async(app_weak: Weak<MainWindow>, action: InstallerAction, text: TextPack) {
    let Some(handle) = app_weak.upgrade() else {
        return;
    };

    if ACTION_RUNNING
        .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
        .is_err()
    {
        return;
    }

    handle.set_phase(InstallerPhase::Working as i32);
    handle.set_busy(true);
    handle.set_progress(0.04);
    handle.set_show_details(false);
    handle.set_state_title(SharedString::from(match action {
        InstallerAction::Install => text.preparing_install,
        InstallerAction::Repair => text.preparing_repair,
        InstallerAction::Uninstall => text.preparing_uninstall,
    }));
    handle.set_state_message(SharedString::from(text.checking_message));

    thread::spawn(move || {
        let result = run_action(&app_weak, action, text);
        let installed = final_installed_state(action, &result, is_package_installed());
        ACTION_RUNNING.store(false, Ordering::SeqCst);

        app_weak
            .upgrade_in_event_loop(move |handle| {
                handle.set_busy(false);
                handle.set_installed(installed);
                handle.set_details_text(SharedString::from(
                    result.as_ref().err().map(String::as_str).unwrap_or(""),
                ));

                match result {
                    Ok(message) => {
                        handle.set_phase(InstallerPhase::Completed as i32);
                        handle.set_progress(1.0);
                        handle.set_state_title(SharedString::from(message));
                        handle.set_state_message(SharedString::from(if installed {
                            text.installed_message
                        } else {
                            text.not_installed_message
                        }));
                    }
                    Err(error) => {
                        handle.set_phase(InstallerPhase::Failed as i32);
                        handle.set_progress(0.0);
                        handle.set_state_title(SharedString::from(text.failed_title));
                        handle.set_state_message(SharedString::from(error.as_str()));
                    }
                }
            })
            .ok();
    });
}

/// Performs the requested package action.
fn run_action(
    app_weak: &Weak<MainWindow>,
    action: InstallerAction,
    text: TextPack,
) -> Result<&'static str, String> {
    let context = build_context()?;
    inspect_supported_system()?;
    ensure_interactive_parent_context()?;
    let target_sid = query_current_user_sid()?;
    let mut package_mutation_locks =
        acquire_installer_mutation_locks(&target_sid, context.is_installed)?;
    let operation = match action {
        InstallerAction::Install => OperationAction::Install,
        InstallerAction::Repair => OperationAction::Repair,
        InstallerAction::Uninstall => OperationAction::Uninstall,
    };
    let trusted_payload = if matches!(operation, OperationAction::Uninstall) {
        None
    } else {
        Some(verify_installer_payload(&context.payload_dir)?)
    };
    for step in operation_steps(operation) {
        match step {
            OperationStep::InstallCurrentUserCertificate => {
                set_progress(
                    app_weak,
                    if matches!(action, InstallerAction::Repair) {
                        0.28
                    } else {
                        0.22
                    },
                    text.certificate_title,
                    text.certificate_message,
                );
                install_certificate(
                    trusted_payload
                        .as_ref()
                        .expect("non-uninstall operation has a trust anchor")
                        .certificate(),
                    package_mutation_locks.installer_mutation_path(),
                )?;
            }
            OperationStep::PrepareMachineInstall => {
                set_progress(app_weak, 0.42, text.package_title, text.package_message);
                if prepare_machine_transaction(&target_sid, false)? {
                    return Err(String::from(
                        "installer.machine.owner_conflict: The machine service belongs to another user. Run Repair to explicitly re-associate it.",
                    ));
                }
            }
            OperationStep::PrepareMachineRepair => {
                set_progress(app_weak, 0.42, text.package_title, text.package_message);
                let repair_required = prepare_machine_transaction(&target_sid, true)?;
                debug_assert!(!repair_required);
            }
            OperationStep::DeployCurrentUserPackageInPlace => {
                set_progress(app_weak, 0.62, text.package_title, text.package_message);
                let update_existing =
                    matches!(action, InstallerAction::Repair) && context.is_installed;
                if let Err(error) = deploy_package(
                    trusted_payload
                        .as_ref()
                        .expect("non-uninstall operation has a trust anchor")
                        .primary_msix(),
                    trusted_payload
                        .as_ref()
                        .expect("non-uninstall operation has a trust anchor")
                        .dependencies(),
                    update_existing,
                    package_mutation_locks.installer_mutation_path(),
                ) {
                    return Err(format!(
                        "{error}\ninstaller.transaction.package_state_uncertain: The transaction was retained. Run the same Installer and choose Repair."
                    ));
                }
                package_mutation_locks.acquire_recovery_lock(&target_sid)?;
            }
            OperationStep::CommitMachineTransaction => {
                set_progress(app_weak, 0.86, text.package_title, text.package_message);
                commit_machine_transaction(&target_sid)?;
            }
            OperationStep::RemoveMachineResourcesIfOwner => {
                set_progress(app_weak, 0.30, text.removing_title, text.removing_message);
                uninstall_machine_resources_if_owner(&target_sid)?;
            }
            OperationStep::RemoveCurrentUserStartupFallback => {
                uninstall_startup_restore_fallback(
                    package_mutation_locks.installer_mutation_path(),
                )?;
            }
            OperationStep::RemoveCurrentUserPackageIfPresent => {
                if context.is_installed {
                    // The package-independent lock still blocks the current App while allowing
                    // Remove-AppxPackage to delete LocalState and its recovery-lock file.
                    package_mutation_locks.release_recovery_lock();
                    ensure_current_user_package_stopped(&target_sid)?;
                    set_progress(app_weak, 0.62, text.uninstall_title, text.uninstall_message);
                    uninstall_package(package_mutation_locks.installer_mutation_path())?;
                }
            }
        }
    }

    Ok(match action {
        InstallerAction::Install => text.installed_done,
        InstallerAction::Repair => text.repaired_done,
        InstallerAction::Uninstall => text.uninstalled_done,
    })
}

/// Returns the UI installation state after an action using a fresh package query result.
fn final_installed_state(
    _action: InstallerAction,
    _result: &Result<&'static str, String>,
    currently_installed: bool,
) -> bool {
    currently_installed
}

/// Builds installer context from files located next to the running executable.
fn build_context() -> Result<InstallerContext, String> {
    let exe_dir = std::env::current_exe()
        .map_err(|error| format!("current exe path failed: {error}"))?
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| String::from("installer directory could not be resolved"))?;
    let payload_dir = exe_dir.join("payload");
    let package_path = find_top_level_payload_file(&payload_dir, &["msixbundle", "msix"]);
    let certificate_path = find_payload_file(&payload_dir, &["cer"]);
    let dependency_paths = find_dependency_packages(&payload_dir);

    Ok(InstallerContext {
        payload_dir,
        package_path,
        certificate_path,
        dependency_paths,
        is_installed: is_package_installed(),
    })
}

/// Verifies that the current device can run the Clash# MSIX package.
fn inspect_supported_system() -> Result<SystemInfo, String> {
    if !cfg!(target_pointer_width = "64") {
        return Err(String::from("Installer must run as a 64-bit process."));
    }

    let architecture = require_native_amd64(query_native_machine()?)?.to_owned();

    let build = read_windows_build()?;
    if build < 22000 {
        return Err(format!(
            "Windows 11 build 22000 or later is required. Current build: {build}."
        ));
    }

    Ok(SystemInfo {
        build,
        architecture,
    })
}

/// Returns the native hardware machine type, unaffected by inherited environment variables.
fn query_native_machine() -> Result<u16, String> {
    #[cfg(windows)]
    {
        unsafe extern "system" {
            fn GetCurrentProcess() -> *mut std::ffi::c_void;
            fn IsWow64Process2(
                process: *mut std::ffi::c_void,
                process_machine: *mut u16,
                native_machine: *mut u16,
            ) -> i32;
        }

        let mut process_machine = 0_u16;
        let mut native_machine = 0_u16;
        // SAFETY: GetCurrentProcess returns a valid pseudo-handle, and both output pointers
        // reference writable u16 values for the duration of the call.
        let succeeded = unsafe {
            IsWow64Process2(
                GetCurrentProcess(),
                &raw mut process_machine,
                &raw mut native_machine,
            )
        };
        if succeeded == 0 {
            return Err(format!(
                "installer.architecture.query_failed: {}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(native_machine)
    }
    #[cfg(not(windows))]
    {
        Err(String::from(
            "installer.architecture.query_failed: Windows API unavailable",
        ))
    }
}

/// Accepts only native AMD64 hardware; x64 emulation on ARM64 is intentionally unsupported.
fn require_native_amd64(native_machine: u16) -> Result<&'static str, String> {
    match native_machine {
        IMAGE_FILE_MACHINE_AMD64 => Ok("AMD64"),
        IMAGE_FILE_MACHINE_ARM64 => Err(String::from(
            "Windows 11 on native AMD64/x64 hardware is required; ARM64 is not supported.",
        )),
        value => Err(format!(
            "Windows 11 on native AMD64/x64 hardware is required; native machine 0x{value:04x} is unsupported."
        )),
    }
}

/// Reads the Windows build number from the registry.
fn read_windows_build() -> Result<u32, String> {
    let output = hidden_command(trusted_system_directory()?.join("reg.exe"))
        .args([
            "query",
            r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "/v",
            "CurrentBuildNumber",
        ])
        .output()
        .map_err(|error| format!("Windows version query failed: {error}"))?;
    let text = String::from_utf8_lossy(&output.stdout);

    text.split_whitespace()
        .filter_map(|part| part.parse::<u32>().ok())
        .next_back()
        .ok_or_else(|| String::from("Windows build number could not be read."))
}

/// Imports the package signing certificate into the current user's trusted people store.
fn install_certificate(certificate_path: &Path, barrier_path: &Path) -> Result<(), String> {
    run_parent_mutating_powershell(
        &format!(
            "Import-Certificate -FilePath {} -CertStoreLocation Cert:\\CurrentUser\\TrustedPeople | Out-Null",
            powershell_quote(certificate_path)
        ),
        barrier_path,
    )
}

/// Deploys the MSIX package with any runtime dependency packages found in payload.
fn deploy_package(
    package_path: &Path,
    dependency_paths: &[PathBuf],
    update_existing: bool,
    barrier_path: &Path,
) -> Result<(), String> {
    run_parent_mutating_powershell(
        &render_deploy_package_command(package_path, dependency_paths, update_existing),
        barrier_path,
    )
}

/// Builds the CurrentUser Add-Appx command so repair semantics can be unit tested without deployment.
fn render_deploy_package_command(
    package_path: &Path,
    dependency_paths: &[PathBuf],
    update_existing: bool,
) -> String {
    let update_options = if update_existing {
        " -Update -ForceUpdateFromAnyVersion -RetainFilesOnFailure"
    } else {
        ""
    };
    if dependency_paths.is_empty() {
        return format!(
            "Add-AppxPackage -Path {} -ForceApplicationShutdown{}",
            powershell_quote(package_path),
            update_options,
        );
    }

    let dependencies = dependency_paths
        .iter()
        .map(|path| powershell_quote(path))
        .collect::<Vec<_>>()
        .join(",");

    format!(
        "Add-AppxPackage -Path {} -DependencyPath @({}) -ForceApplicationShutdown{}",
        powershell_quote(package_path),
        dependencies,
        update_options,
    )
}

/// Removes the installed Clash# MSIX package for the current user.
fn uninstall_package(barrier_path: &Path) -> Result<(), String> {
    run_parent_mutating_powershell(
        &format!(
            "$pkg = Get-AppxPackage -Name {}; if ($null -ne $pkg) {{ Remove-AppxPackage -Package $pkg.PackageFullName }}",
            powershell_quote_text(PACKAGE_IDENTITY_NAME)
        ),
        barrier_path,
    )
}

/// Ensures CurrentUser package operations remain in the target user's non-elevated context.
fn ensure_interactive_parent_context() -> Result<(), String> {
    if is_current_process_elevated()? {
        return Err(String::from(
            "installer.parent_context.elevated: Run the installer normally. It requests elevation only for the machine service helper.",
        ));
    }
    Ok(())
}

/// Resolves and validates the target SID before any UAC credential switch.
fn query_current_user_sid() -> Result<String, String> {
    let output = run_powershell_capture(
        "$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; \
         [Console]::Out.Write($sid)",
    )?;
    let sid = successful_output_text(output, "installer.target_sid.query_failed")?;
    validate_owner_sid(&sid)?;
    Ok(sid)
}

/// Acquires the per-user operation lock and App startup reader, then the legacy recovery lock.
fn acquire_installer_mutation_locks(
    target_sid: &str,
    package_installed: bool,
) -> Result<PackageMutationLocks, String> {
    ensure_current_user_package_stopped(target_sid)?;
    let installer_mutation_path = query_installer_mutation_lock_path()?;
    ensure_package_independent_lock_directory(&installer_mutation_path)?;
    let operation_lock = open_exclusive_coordination_lock(
        &query_installer_operation_lock_path()?,
        "installer.operation.busy: Another Installer operation is already running for this user.",
    )?;
    let installer_mutation_reader =
        prepare_installer_mutation_reader(&installer_mutation_path, target_sid)?;
    ensure_current_user_package_stopped(target_sid)?;
    let mut locks = PackageMutationLocks {
        _operation_lock: operation_lock,
        _installer_mutation_reader: installer_mutation_reader,
        installer_mutation_path,
        recovery_lock: None,
    };
    if package_installed {
        locks.acquire_recovery_lock(target_sid)?;
    }

    Ok(locks)
}

/// Creates only the fixed ClashSharp coordination directory and rejects reparse traversal.
fn ensure_package_independent_lock_directory(lock_path: &Path) -> Result<(), String> {
    let lock_directory = lock_path
        .parent()
        .ok_or_else(|| String::from("installer.app.lock_path_invalid"))?;
    let local_app_data = lock_directory
        .parent()
        .ok_or_else(|| String::from("installer.app.lock_path_invalid"))?;
    validate_ordinary_directory_chain(local_app_data, "installer.app.lock_directory_unsafe")?;
    match std::fs::create_dir(lock_directory) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
        Err(error) => {
            return Err(format!("installer.app.lock_directory_failed: {error}"));
        }
    }
    validate_ordinary_directory_chain(lock_directory, "installer.app.lock_directory_unsafe")
}

/// Temporarily denies sharing while applying the fixed DACL, then reopens as a shared reader.
fn prepare_installer_mutation_reader(lock_path: &Path, target_sid: &str) -> Result<File, String> {
    validate_owner_sid(target_sid)?;
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true);
    #[cfg(windows)]
    {
        const GENERIC_READ: u32 = 0x8000_0000;
        const GENERIC_WRITE: u32 = 0x4000_0000;
        const WRITE_DAC: u32 = 0x0004_0000;
        options
            .access_mode(GENERIC_READ | GENERIC_WRITE | WRITE_DAC)
            .share_mode(0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    }
    let preparer = options.open(lock_path).map_err(|error| {
        coordination_lock_open_error(
            error,
            "installer.operation.busy: Clash# is starting/running or another lock preparation is active.",
        )
    })?;
    validate_coordination_lock_file(&preparer)?;
    set_installer_mutation_lock_dacl(&preparer, target_sid)?;
    drop(preparer);

    open_installer_mutation_reader(lock_path)
}

/// Opens the App startup barrier with read access and read sharing only.
fn open_installer_mutation_reader(lock_path: &Path) -> Result<File, String> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    options
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    let reader = options.open(lock_path).map_err(|error| {
        coordination_lock_open_error(
            error,
            "installer.operation.busy: Clash# acquired its startup barrier; close it and retry.",
        )
    })?;
    validate_coordination_lock_file(&reader)?;
    Ok(reader)
}

fn validate_coordination_lock_file(file: &File) -> Result<(), String> {
    let metadata = file
        .metadata()
        .map_err(|error| format!("installer.app.lock_metadata_failed: {error}"))?;
    if !metadata.is_file() || metadata_is_reparse_point(&metadata) {
        return Err(String::from("installer.app.lock_file_unsafe"));
    }
    Ok(())
}

fn coordination_lock_open_error(error: std::io::Error, busy_message: &str) -> String {
    if matches!(error.raw_os_error(), Some(32 | 33)) {
        busy_message.to_owned()
    } else {
        format!("installer.app.lock_failed: {error}")
    }
}

#[cfg(windows)]
struct LocalSecurityDescriptor(*mut std::ffi::c_void);

#[cfg(windows)]
impl Drop for LocalSecurityDescriptor {
    fn drop(&mut self) {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn LocalFree(memory: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
        }

        // SAFETY: the pointer was allocated by ConvertStringSecurityDescriptor... and is
        // released exactly once by this guard.
        let _ = unsafe { LocalFree(self.0) };
    }
}

#[cfg(windows)]
fn local_security_descriptor_from_sddl(sddl: &str) -> Result<LocalSecurityDescriptor, String> {
    const SDDL_REVISION_1: u32 = 1;

    #[link(name = "advapi32")]
    unsafe extern "system" {
        fn ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string_security_descriptor: *const u16,
            string_sd_revision: u32,
            security_descriptor: *mut *mut std::ffi::c_void,
            security_descriptor_size: *mut u32,
        ) -> i32;
    }

    let mut encoded = std::ffi::OsStr::new(sddl).encode_wide().collect::<Vec<_>>();
    if encoded.contains(&0) {
        return Err(String::from("installer.security_descriptor.invalid"));
    }
    encoded.push(0);
    let mut security_descriptor = std::ptr::null_mut();
    // SAFETY: encoded is NUL terminated and the output pointer is writable for this call.
    let succeeded = unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            encoded.as_ptr(),
            SDDL_REVISION_1,
            &raw mut security_descriptor,
            std::ptr::null_mut(),
        )
    };
    if succeeded == 0 || security_descriptor.is_null() {
        return Err(format!(
            "installer.security_descriptor.invalid: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(LocalSecurityDescriptor(security_descriptor))
}

/// Applies a protected DACL through the already-open file handle.
fn set_installer_mutation_lock_dacl(file: &File, target_sid: &str) -> Result<(), String> {
    #[cfg(windows)]
    {
        const SE_FILE_OBJECT: u32 = 1;
        const DACL_SECURITY_INFORMATION: u32 = 0x0000_0004;
        const PROTECTED_DACL_SECURITY_INFORMATION: u32 = 0x8000_0000;

        #[link(name = "advapi32")]
        unsafe extern "system" {
            fn GetSecurityDescriptorDacl(
                security_descriptor: *mut std::ffi::c_void,
                dacl_present: *mut i32,
                dacl: *mut *mut std::ffi::c_void,
                dacl_defaulted: *mut i32,
            ) -> i32;
            fn SetSecurityInfo(
                handle: *mut std::ffi::c_void,
                object_type: u32,
                security_information: u32,
                owner: *mut std::ffi::c_void,
                group: *mut std::ffi::c_void,
                dacl: *mut std::ffi::c_void,
                sacl: *mut std::ffi::c_void,
            ) -> u32;
        }

        let sddl = format!("D:P(A;;GA;;;{target_sid})(A;;GR;;;SY)(A;;GR;;;BA)");
        let security_descriptor = local_security_descriptor_from_sddl(&sddl)?;
        let mut dacl_present = 0_i32;
        let mut dacl_defaulted = 0_i32;
        let mut dacl = std::ptr::null_mut();
        // SAFETY: the converted descriptor remains allocated, and all output pointers are valid.
        let dacl_succeeded = unsafe {
            GetSecurityDescriptorDacl(
                security_descriptor.0,
                &raw mut dacl_present,
                &raw mut dacl,
                &raw mut dacl_defaulted,
            )
        };
        if dacl_succeeded == 0 || dacl_present == 0 || dacl.is_null() {
            return Err(format!(
                "installer.app.lock_dacl_invalid: {}",
                std::io::Error::last_os_error()
            ));
        }
        // SAFETY: the handle is live and was opened with WRITE_DAC; the DACL is owned by the
        // converted descriptor and remains live for the duration of this call.
        let error = unsafe {
            SetSecurityInfo(
                file.as_raw_handle(),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                dacl,
                std::ptr::null_mut(),
            )
        };
        if error != 0 {
            return Err(format!(
                "installer.app.lock_dacl_failed: {}",
                std::io::Error::from_raw_os_error(error as i32)
            ));
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let _ = (file, target_sid);
        Err(String::from(
            "installer.app.lock_dacl_failed: Windows API unavailable",
        ))
    }
}

fn open_exclusive_coordination_lock(lock_path: &Path, busy_message: &str) -> Result<File, String> {
    let lock_directory = lock_path
        .parent()
        .ok_or_else(|| String::from("installer.app.lock_path_invalid"))?;
    std::fs::create_dir_all(lock_directory)
        .map_err(|error| format!("installer.app.lock_directory_failed: {error}"))?;

    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true);
    #[cfg(windows)]
    options.share_mode(0);
    options
        .open(lock_path)
        .map_err(|error| coordination_lock_open_error(error, busy_message))
}

fn query_current_user_local_app_data() -> Result<PathBuf, String> {
    let output = run_powershell_capture(
        "$path = [Environment]::GetFolderPath(\
             [Environment+SpecialFolder]::LocalApplicationData); \
         [Console]::Out.Write($path)",
    )?;
    let path = PathBuf::from(successful_output_text(
        output,
        "installer.app.local_data_query_failed",
    )?);
    if !path.is_absolute() {
        return Err(String::from("installer.app.local_data_query_invalid"));
    }

    Ok(path)
}

/// Resolves the package-independent lock acquired before the App creates its shell or core.
fn query_installer_mutation_lock_path() -> Result<PathBuf, String> {
    Ok(query_current_user_local_app_data()?
        .join("ClashSharp")
        .join("InstallerMutation.lock"))
}

/// Resolves the per-user exclusive lock that serializes Installer parents.
fn query_installer_operation_lock_path() -> Result<PathBuf, String> {
    Ok(query_current_user_local_app_data()?
        .join("ClashSharp")
        .join("InstallerOperation.lock"))
}

/// Resolves the same package LocalState lock acquired before Clash# creates its shell or core.
fn query_current_user_recovery_lock_path() -> Result<PathBuf, String> {
    Ok(query_current_user_local_app_data()?
        .join("Packages")
        .join(PACKAGE_FAMILY_NAME)
        .join("LocalState")
        .join("RecoveryWatchdog.lock"))
}

/// Refuses package and machine changes while this user's exact registered app is still running.
fn ensure_current_user_package_stopped(target_sid: &str) -> Result<(), String> {
    let command = render_package_process_preflight_command(target_sid, false)?;
    if package_process_is_running(&command)? {
        return Err(String::from(
            "installer.app.running: Close Clash# completely (including its tray app), then retry.",
        ));
    }

    Ok(())
}

/// Rechecks the exact target user's package from the elevated helper after the UAC wait.
fn target_user_package_is_running(target_sid: &str) -> Result<bool, String> {
    let command = render_package_process_preflight_command(target_sid, true)?;
    package_process_is_running(&command)
}

fn package_process_is_running(command: &str) -> Result<bool, String> {
    let output = run_powershell_capture(command)?;
    match output.status.code() {
        Some(0) => Ok(false),
        Some(APP_RUNNING_EXIT_CODE) => Ok(true),
        _ => successful_output_text(output, "installer.app.preflight_failed").map(|_| false),
    }
}

/// Anchors process detection to an exact package registration and token SID.
fn render_package_process_preflight_command(
    target_sid: &str,
    query_target_user: bool,
) -> Result<String, String> {
    validate_owner_sid(target_sid)?;
    let package_query = if query_target_user {
        "$packages = @(Get-AppxPackage -User $targetSid -Name $identityName)"
    } else {
        "$packages = @(Get-AppxPackage -Name $identityName)"
    };
    Ok(PACKAGE_PROCESS_PREFLIGHT_TEMPLATE
        .replace("@@TARGET_SID@@", &powershell_quote_text(target_sid))
        .replace("@@PACKAGE_QUERY@@", package_query)
        .replace(
            "@@PACKAGE_IDENTITY_NAME@@",
            &powershell_quote_text(PACKAGE_IDENTITY_NAME),
        )
        .replace(
            "@@PACKAGE_FAMILY_NAME@@",
            &powershell_quote_text(PACKAGE_FAMILY_NAME),
        )
        .replace(
            "@@APP_RUNNING_EXIT_CODE@@",
            &APP_RUNNING_EXIT_CODE.to_string(),
        ))
}

const PACKAGE_PROCESS_PREFLIGHT_TEMPLATE: &str = r#"
$ErrorActionPreference = 'Stop'
try {
    $targetSid = @@TARGET_SID@@
    $identityName = @@PACKAGE_IDENTITY_NAME@@
    $expectedFamilyName = @@PACKAGE_FAMILY_NAME@@
    @@PACKAGE_QUERY@@
    if ($packages.Count -eq 0) { exit 0 }
    if ($packages.Count -ne 1) { throw 'current-user package registration is ambiguous' }

    $package = $packages[0]
    $packageFullName = [string]$package.PackageFullName
    $installRoot = [IO.Path]::GetFullPath([string]$package.InstallLocation).TrimEnd([char]92)
    if ([string]::IsNullOrWhiteSpace($packageFullName) -or
        [string]::IsNullOrWhiteSpace($installRoot) -or
        [string]$package.Name -cne $identityName -or
        [string]$package.PackageFamilyName -cne $expectedFamilyName -or
        -not [IO.Path]::IsPathRooted($installRoot) -or
        -not ([IO.Path]::GetFileName($installRoot)).Equals(
            $packageFullName, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'current-user package registration is not exact'
    }
    $installPrefix = $installRoot + [IO.Path]::DirectorySeparatorChar
    $running = [Collections.Generic.List[uint32]]::new()

    foreach ($process in @(Get-CimInstance -ClassName Win32_Process)) {
        $candidatePath = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($candidatePath)) { continue }
        try {
            $candidatePath = [IO.Path]::GetFullPath($candidatePath)
        } catch {
            if ($candidatePath.StartsWith(
                    $installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "registered-package process path is ambiguous: $($process.ProcessId)"
            }
            continue
        }
        if (-not $candidatePath.StartsWith(
                $installPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }

        try {
            $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
        } catch {
            $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                'ProcessId = ' + [uint32]$process.ProcessId))
            if ($remaining.Count -eq 0) { continue }
            throw "registered-package process token is unreadable: $($process.ProcessId)"
        }
        if ($null -eq $owner -or [uint32]$owner.ReturnValue -ne 0 -or
            [string]::IsNullOrWhiteSpace([string]$owner.Sid)) {
            $remaining = @(Get-CimInstance -ClassName Win32_Process -Filter (
                'ProcessId = ' + [uint32]$process.ProcessId))
            if ($remaining.Count -eq 0) { continue }
            throw "registered-package process token is ambiguous: $($process.ProcessId)"
        }
        if ([string]$owner.Sid -ceq $targetSid) {
            $running.Add([uint32]$process.ProcessId)
        }
    }

    if ($running.Count -gt 0) {
        [Console]::Error.Write('installer.app.running: pid=' + ($running -join ','))
        exit @@APP_RUNNING_EXIT_CODE@@
    }
    exit 0
} catch {
    [Console]::Error.Write('installer.app.preflight_failed: ' + $_.Exception.Message)
    exit 24
}
"#;

/// Returns whether this process currently has an elevated administrator token.
fn is_current_process_elevated() -> Result<bool, String> {
    let output = run_powershell_capture(
        "$identity = [Security.Principal.WindowsIdentity]::GetCurrent(); \
         $principal = [Security.Principal.WindowsPrincipal]::new($identity); \
         [Console]::Out.Write($principal.IsInRole(\
             [Security.Principal.WindowsBuiltInRole]::Administrator).ToString())",
    )?;
    match successful_output_text(output, "installer.elevation.query_failed")?.as_str() {
        "True" => Ok(true),
        "False" => Ok(false),
        _ => Err(String::from("installer.elevation.query_invalid")),
    }
}

/// Durably reserves the machine transaction before any CurrentUser package mutation.
fn prepare_machine_transaction(
    target_sid: &str,
    allow_reassociation: bool,
) -> Result<bool, String> {
    let mode = if allow_reassociation {
        "--machine-prepare-repair"
    } else {
        "--machine-prepare-install"
    };
    match invoke_elevated_machine_helper(&[mode, "--target-sid", target_sid])? {
        0 => Ok(false),
        APP_RUNNING_EXIT_CODE => Err(String::from(
            "installer.app.running: Close Clash# completely (including its tray app), then retry.",
        )),
        MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE => Err(String::from(
            "installer.machine.service_delete_pending_reboot: Restart Windows, then run Repair again.",
        )),
        MACHINE_HELPER_REPAIR_REQUIRED_EXIT_CODE if !allow_reassociation => Ok(true),
        MACHINE_TRANSACTION_CONFLICT_EXIT_CODE => Err(String::from(
            "installer.transaction.conflict: Another release or user owns the pending transaction. Run the same Installer that created it and choose Repair.",
        )),
        code => Err(format!(
            "installer.transaction.prepare_failed: exit code {code}"
        )),
    }
}

/// Independently verifies package registration and rolls a prepared transaction forward.
fn commit_machine_transaction(target_sid: &str) -> Result<(), String> {
    match invoke_elevated_machine_helper(&["--machine-commit", "--target-sid", target_sid])? {
        0 => Ok(()),
        APP_RUNNING_EXIT_CODE => Err(String::from(
            "installer.app.running: Close Clash# completely (including its tray app), then run Repair.",
        )),
        MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE => Err(String::from(
            "installer.machine.service_delete_pending_reboot: Restart Windows, then run Repair again.",
        )),
        MACHINE_HELPER_REPAIR_REQUIRED_EXIT_CODE => Err(String::from(
            "installer.machine.owner_conflict: The reserved ordinary install can no longer commit. Run the same Installer and choose Repair.",
        )),
        MACHINE_TRANSACTION_CONFLICT_EXIT_CODE => Err(String::from(
            "installer.transaction.conflict: The pending transaction does not belong to this release or user.",
        )),
        MACHINE_TRANSACTION_PACKAGE_NOT_COMMITTED_EXIT_CODE => Err(String::from(
            "installer.transaction.package_not_committed: Windows did not register the exact package. Run the same Installer and choose Repair.",
        )),
        code => Err(format!(
            "installer.transaction.commit_failed: exit code {code}"
        )),
    }
}

/// Requests owner-checked deletion; a non-owner helper invocation intentionally succeeds as a no-op.
fn uninstall_machine_resources_if_owner(target_sid: &str) -> Result<(), String> {
    match invoke_elevated_machine_helper(&["--machine-uninstall", "--target-sid", target_sid])? {
        0 => Ok(()),
        APP_RUNNING_EXIT_CODE => Err(String::from(
            "installer.app.running: Close Clash# completely (including its tray app), then retry.",
        )),
        MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE => Err(String::from(
            "installer.machine.service_delete_pending_reboot: Restart Windows, then retry uninstall.",
        )),
        MACHINE_TRANSACTION_CONFLICT_EXIT_CODE => Err(String::from(
            "installer.transaction.pending: Finish the pending transaction with the same Installer and Repair before uninstalling.",
        )),
        code => Err(format!(
            "installer.machine.uninstall_failed: exit code {code}"
        )),
    }
}

/// Runs the same executable through a narrow `runas` boundary and returns its stable exit code.
fn invoke_elevated_machine_helper(arguments: &[&str]) -> Result<i32, String> {
    let executable = std::env::current_exe()
        .map_err(|error| format!("installer.machine_helper.exe_unavailable: {error}"))?;
    let argument_list = arguments
        .iter()
        .map(|argument| powershell_quote_text(argument))
        .collect::<Vec<_>>()
        .join(",");
    let command = format!(
        "$process = Start-Process -FilePath {} -Verb RunAs -WindowStyle Hidden -Wait -PassThru \
         -ArgumentList @({argument_list}); exit $process.ExitCode",
        powershell_quote(&executable),
    );
    let output = run_powershell_capture(&command)?;
    output
        .status
        .code()
        .ok_or_else(|| String::from("installer.machine_helper.no_exit_code"))
}

/// Deletes the current-user startup restore fallback registration.
fn uninstall_startup_restore_fallback(barrier_path: &Path) -> Result<(), String> {
    run_parent_mutating_powershell(
        "Remove-ItemProperty \
             -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' \
             -Name 'ClashSharp.ProxyRestoreFallback' \
             -ErrorAction SilentlyContinue",
        barrier_path,
    )
}

/// Returns whether the Clash# package is installed for the current user.
fn is_package_installed() -> bool {
    run_powershell_capture(&format!(
        "if (Get-AppxPackage -Name {}) {{ exit 0 }} else {{ exit 1 }}",
        powershell_quote_text(PACKAGE_IDENTITY_NAME)
    ))
    .map(|output| output.status.success())
    .unwrap_or(false)
}

/// Executes one already-parsed elevated helper action and returns a stable process exit code.
fn run_machine_helper(invocation: MachineHelperInvocation) -> i32 {
    match execute_machine_helper(&invocation) {
        Ok(code) => code,
        Err(error) => {
            eprintln!("{error}");
            MACHINE_HELPER_FAILURE_EXIT_CODE
        }
    }
}

#[cfg(windows)]
struct MachineMutationMutexGuard {
    handle: *mut std::ffi::c_void,
}

#[cfg(windows)]
impl Drop for MachineMutationMutexGuard {
    fn drop(&mut self) {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn ReleaseMutex(mutex: *mut std::ffi::c_void) -> i32;
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: this guard exists only after a successful/abandoned wait transferred mutex
        // ownership to this process, and it owns the live handle exactly once.
        unsafe {
            let _ = ReleaseMutex(self.handle);
            let _ = CloseHandle(self.handle);
        }
    }
}

#[cfg(not(windows))]
struct MachineMutationMutexGuard;

/// Serializes all machine-service mutation across users and interactive sessions.
fn acquire_machine_mutation_mutex() -> Result<MachineMutationMutexGuard, String> {
    #[cfg(windows)]
    {
        const MUTEX_MODIFY_STATE: u32 = 0x0000_0001;
        const SYNCHRONIZE: u32 = 0x0010_0000;
        const WAIT_OBJECT_0: u32 = 0x0000_0000;
        const WAIT_ABANDONED_0: u32 = 0x0000_0080;
        const WAIT_TIMEOUT: u32 = 0x0000_0102;
        const WAIT_FAILED: u32 = 0xffff_ffff;

        #[repr(C)]
        struct SecurityAttributes {
            length: u32,
            security_descriptor: *mut std::ffi::c_void,
            inherit_handle: i32,
        }

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateMutexExW(
                mutex_attributes: *mut SecurityAttributes,
                name: *const u16,
                flags: u32,
                desired_access: u32,
            ) -> *mut std::ffi::c_void;
            fn WaitForSingleObject(handle: *mut std::ffi::c_void, milliseconds: u32) -> u32;
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        let security_descriptor =
            local_security_descriptor_from_sddl("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)")?;
        let mut security_attributes = SecurityAttributes {
            length: std::mem::size_of::<SecurityAttributes>() as u32,
            security_descriptor: security_descriptor.0,
            inherit_handle: 0,
        };
        let mut name = std::ffi::OsStr::new(MACHINE_MUTATION_MUTEX_NAME)
            .encode_wide()
            .collect::<Vec<_>>();
        name.push(0);
        // SAFETY: the security descriptor and NUL-terminated name remain live for this call.
        let handle = unsafe {
            CreateMutexExW(
                &raw mut security_attributes,
                name.as_ptr(),
                0,
                MUTEX_MODIFY_STATE | SYNCHRONIZE,
            )
        };
        if handle.is_null() {
            return Err(format!(
                "installer.machine.lock_failed: {}",
                std::io::Error::last_os_error()
            ));
        }

        // SAFETY: handle is a live mutex handle with SYNCHRONIZE access.
        let wait_result = unsafe { WaitForSingleObject(handle, MACHINE_MUTATION_MUTEX_WAIT_MS) };
        match wait_result {
            WAIT_OBJECT_0 => Ok(MachineMutationMutexGuard { handle }),
            // Windows transfers mutex ownership on WAIT_ABANDONED. Keep that ownership and
            // reconcile the durable journal under the same guard instead of opening a race.
            WAIT_ABANDONED_0 => Ok(MachineMutationMutexGuard { handle }),
            WAIT_TIMEOUT => {
                // SAFETY: timeout did not transfer ownership; close only this process's handle.
                let _ = unsafe { CloseHandle(handle) };
                Err(String::from("installer.machine.operation_busy"))
            }
            WAIT_FAILED => {
                let error = std::io::Error::last_os_error();
                // SAFETY: the failed wait did not transfer ownership.
                let _ = unsafe { CloseHandle(handle) };
                Err(format!("installer.machine.lock_wait_failed: {error}"))
            }
            value => {
                // SAFETY: an unknown result is treated as non-ownership and fails closed.
                let _ = unsafe { CloseHandle(handle) };
                Err(format!("installer.machine.lock_wait_invalid: {value}"))
            }
        }
    }
    #[cfg(not(windows))]
    {
        Err(String::from(
            "installer.machine.lock_failed: Windows API unavailable",
        ))
    }
}

/// Performs only fixed machine work after independently resolving the target package and profile.
fn execute_machine_helper(invocation: &MachineHelperInvocation) -> Result<i32, String> {
    inspect_supported_system()?;
    if !is_current_process_elevated()? {
        return Err(String::from("installer.machine_helper.elevation_required"));
    }

    let target_sid = invocation.target_sid();
    validate_owner_sid(target_sid)?;
    let _machine_mutation_guard = acquire_machine_mutation_mutex()?;
    let _installer_mutation_reader = acquire_target_user_installer_mutation_reader(target_sid)?;

    let (program_files, common_application_data) = query_machine_folders()?;
    let resources = MachineResourcePlan::new(&program_files, &common_application_data)?;
    let transaction_path = common_application_data.join(INSTALLER_TRANSACTION_RELATIVE_PATH);
    match invocation {
        MachineHelperInvocation::PrepareInstall { target_sid } => prepare_installer_transaction(
            target_sid,
            false,
            &resources,
            &common_application_data,
            &transaction_path,
        ),
        MachineHelperInvocation::PrepareRepair { target_sid } => prepare_installer_transaction(
            target_sid,
            true,
            &resources,
            &common_application_data,
            &transaction_path,
        ),
        MachineHelperInvocation::Commit { target_sid } => commit_installer_transaction(
            target_sid,
            &resources,
            &program_files,
            &common_application_data,
            &transaction_path,
        ),
        MachineHelperInvocation::Uninstall { target_sid } => {
            if !matches!(
                read_installer_transaction(&transaction_path),
                InstallerTransactionState::Missing
            ) {
                return Ok(MACHINE_TRANSACTION_CONFLICT_EXIT_CODE);
            }
            let association = read_machine_association(&resources);
            if !may_uninstall_machine(target_sid, &association)? {
                return Ok(0);
            }
            if target_user_package_is_running(target_sid)? {
                return Ok(APP_RUNNING_EXIT_CODE);
            }
            let script_exit_code =
                run_powershell_stdin(&resources.render_uninstall_script(target_sid)?)?;
            Ok(script_exit_code)
        }
    }
}

/// Creates or resumes the durable reservation before the parent may call Add-AppxPackage.
fn prepare_installer_transaction(
    target_sid: &str,
    allow_reassociation: bool,
    resources: &MachineResourcePlan,
    common_application_data: &Path,
    transaction_path: &Path,
) -> Result<i32, String> {
    let payload_directory = fixed_sibling_payload_directory()?;
    verify_installer_payload(&payload_directory)?;
    let expected_version = trusted_package_version()?;
    let payload_hash = trusted_msix_sha256()?;

    match read_installer_transaction(transaction_path) {
        InstallerTransactionState::Invalid => {
            return Ok(MACHINE_TRANSACTION_CONFLICT_EXIT_CODE);
        }
        InstallerTransactionState::Valid(mut journal) => {
            verify_installer_transaction_protection(transaction_path)?;
            if journal.target_sid() != target_sid
                || journal.expected_package_version() != expected_version
                || journal.installer_payload_sha256() != payload_hash
            {
                return Ok(MACHINE_TRANSACTION_CONFLICT_EXIT_CODE);
            }
            if allow_reassociation
                && !journal.allow_reassociation()
                && matches!(
                    journal.phase(),
                    InstallerTransactionPhase::Prepared
                        | InstallerTransactionPhase::PackageCommitted
                )
            {
                journal.upgrade_to_explicit_repair()?;
                write_installer_transaction(common_application_data, transaction_path, &journal)?;
            }
            return Ok(0);
        }
        InstallerTransactionState::Missing => {}
    }

    let association = read_machine_association(resources);
    let previous_owner_sid = match &association {
        AssociationState::Valid(previous) if previous.owner_sid() != target_sid => {
            Some(previous.owner_sid().to_owned())
        }
        _ => None,
    };
    let _previous_owner_mutation_reader = previous_owner_sid
        .as_deref()
        .map(acquire_target_user_installer_mutation_reader)
        .transpose()?;
    let service_exists = query_mihomo_service_exists()?;
    let machine_residue_exists = service_exists
        || resources.machine_root().exists()
        || resources.service_data_root().exists();
    if matches!(
        decide_apply_for_owner(
            target_sid,
            allow_reassociation,
            &association,
            service_exists,
            machine_residue_exists,
            &generate_token()?,
        )?,
        ApplyDecision::RequiresExplicitRepair
    ) {
        return Ok(MACHINE_HELPER_REPAIR_REQUIRED_EXIT_CODE);
    }
    if let Some(previous_owner_sid) = previous_owner_sid.as_deref()
        && target_user_package_is_running(previous_owner_sid)?
    {
        return Ok(APP_RUNNING_EXIT_CODE);
    }
    if target_user_package_is_running(target_sid)? {
        return Ok(APP_RUNNING_EXIT_CODE);
    }

    let journal = InstallerTransactionJournal::create(
        target_sid,
        allow_reassociation,
        expected_version,
        payload_hash,
    )?;
    write_installer_transaction(common_application_data, transaction_path, &journal)?;
    Ok(0)
}

/// Independently proves the target package, then performs only repeatable roll-forward work.
fn commit_installer_transaction(
    target_sid: &str,
    resources: &MachineResourcePlan,
    program_files: &Path,
    common_application_data: &Path,
    transaction_path: &Path,
) -> Result<i32, String> {
    let payload_directory = fixed_sibling_payload_directory()?;
    let trusted_payload = verify_installer_payload(&payload_directory)?;
    let expected_version = trusted_package_version()?;
    let payload_hash = trusted_msix_sha256()?;
    let InstallerTransactionState::Valid(mut journal) =
        read_installer_transaction(transaction_path)
    else {
        return Ok(MACHINE_TRANSACTION_CONFLICT_EXIT_CODE);
    };
    verify_installer_transaction_protection(transaction_path)?;
    if journal.target_sid() != target_sid
        || journal.expected_package_version() != expected_version
        || journal.installer_payload_sha256() != payload_hash
    {
        return Ok(MACHINE_TRANSACTION_CONFLICT_EXIT_CODE);
    }

    let Some(registration) = query_target_package_registration_if_present(target_sid)? else {
        return Ok(MACHINE_TRANSACTION_PACKAGE_NOT_COMMITTED_EXIT_CODE);
    };
    if !registration.matches_trusted_package_version(expected_version)?
        || verify_registered_machine_payload(registration.install_location()).is_err()
    {
        return Ok(MACHINE_TRANSACTION_PACKAGE_NOT_COMMITTED_EXIT_CODE);
    }
    if journal.phase() == InstallerTransactionPhase::Prepared {
        journal.transition_to(InstallerTransactionPhase::PackageCommitted)?;
        write_installer_transaction(common_application_data, transaction_path, &journal)?;
    }

    let association = read_machine_association(resources);
    let previous_owner_sid = match &association {
        AssociationState::Valid(previous) if previous.owner_sid() != target_sid => {
            Some(previous.owner_sid().to_owned())
        }
        _ => None,
    };
    let _previous_owner_mutation_reader = previous_owner_sid
        .as_deref()
        .map(acquire_target_user_installer_mutation_reader)
        .transpose()?;
    let service_exists = query_mihomo_service_exists()?;
    let machine_residue_exists = service_exists
        || resources.machine_root().exists()
        || resources.service_data_root().exists();
    let decision = decide_apply_for_owner(
        target_sid,
        journal.allow_reassociation(),
        &association,
        service_exists,
        machine_residue_exists,
        &generate_token()?,
    )?;
    let ApplyDecision::Provision {
        authentication_token,
    } = decision
    else {
        return Ok(MACHINE_HELPER_REPAIR_REQUIRED_EXIT_CODE);
    };
    if let Some(previous_owner_sid) = previous_owner_sid.as_deref()
        && target_user_package_is_running(previous_owner_sid)?
    {
        return Ok(APP_RUNNING_EXIT_CODE);
    }
    if target_user_package_is_running(target_sid)? {
        return Ok(APP_RUNNING_EXIT_CODE);
    }

    let trusted_manifest = trusted_machine_manifest_json()?;
    let plan = MachineServicePlan::new(
        &registration,
        program_files,
        common_application_data,
        MachineTransactionContext::new(
            journal.transaction_id(),
            target_sid,
            previous_owner_sid.as_deref(),
            &authentication_token,
        ),
        MachinePayloadSource::new(
            trusted_payload.primary_msix(),
            payload_hash,
            &trusted_manifest,
        ),
    )?;
    let script_exit_code = run_powershell_stdin(&plan.render_apply_script()?)?;
    if script_exit_code != 0 {
        return Ok(script_exit_code);
    }
    let committed_association = read_machine_association(resources);
    if !matches!(
        &committed_association,
        AssociationState::Valid(value) if value.owner_sid() == target_sid
    ) || !query_mihomo_service_is_running()?
    {
        return Err(String::from(
            "installer.transaction.machine_verification_failed",
        ));
    }

    if journal.phase() == InstallerTransactionPhase::PackageCommitted {
        journal.transition_to(InstallerTransactionPhase::MachineCommitted)?;
        write_installer_transaction(common_application_data, transaction_path, &journal)?;
    }
    if journal.phase() == InstallerTransactionPhase::MachineCommitted {
        journal.transition_to(InstallerTransactionPhase::Verified)?;
        write_installer_transaction(common_application_data, transaction_path, &journal)?;
    }
    if journal.phase() != InstallerTransactionPhase::Verified {
        return Err(String::from("installer.transaction.phase_invalid"));
    }
    clear_installer_transaction(common_application_data, transaction_path)?;
    Ok(0)
}

/// Resolves the only payload directory accepted by the elevated copy of this executable.
fn fixed_sibling_payload_directory() -> Result<PathBuf, String> {
    std::env::current_exe()
        .map_err(|error| format!("installer.machine_helper.exe_unavailable: {error}"))?
        .parent()
        .map(|directory| directory.join("payload"))
        .ok_or_else(|| String::from("installer.machine_helper.payload_unavailable"))
}

/// Resolves fixed machine folders without trusting inherited environment variables.
fn query_machine_folders() -> Result<(PathBuf, PathBuf), String> {
    let output = run_powershell_capture(
        "$programFiles = [Environment]::GetFolderPath(\
             [Environment+SpecialFolder]::ProgramFiles); \
         $programData = [Environment]::GetFolderPath(\
             [Environment+SpecialFolder]::CommonApplicationData); \
         [Console]::Out.WriteLine($programFiles); \
         [Console]::Out.Write($programData)",
    )?;
    let text = successful_output_text(output, "installer.machine.folders_query_failed")?;
    let lines = text.lines().collect::<Vec<_>>();
    if lines.len() != 2 || lines.iter().any(|line| line.trim().is_empty()) {
        return Err(String::from("installer.machine.folders_query_invalid"));
    }
    Ok((PathBuf::from(lines[0]), PathBuf::from(lines[1])))
}

/// Resolves the interactive target profile only from the canonical SID's ProfileList entry.
fn query_target_user_profile_path(target_sid: &str) -> Result<PathBuf, String> {
    validate_owner_sid(target_sid)?;
    let command = format!(
        "$sidText = {}; \
         $sid = [Security.Principal.SecurityIdentifier]::new($sidText); \
         if ($sid.Value -cne $sidText) {{ throw 'target SID is not canonical' }}; \
         $profileKey = 'Registry::HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\' + $sidText; \
         $profilePath = [Environment]::ExpandEnvironmentVariables(\
             [string](Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath); \
         [Console]::Out.Write($profilePath)",
        powershell_quote_text(target_sid),
    );
    let output = run_powershell_capture(&command)?;
    let text = successful_output_text(output, "installer.target_profile.query_failed")?;
    let profile_path = PathBuf::from(&text);
    if text.is_empty()
        || text.chars().any(char::is_control)
        || !profile_path.is_absolute()
        || profile_path.components().any(|component| {
            matches!(
                component,
                std::path::Component::CurDir | std::path::Component::ParentDir
            )
        })
    {
        return Err(String::from("installer.target_profile.query_invalid"));
    }
    Ok(profile_path)
}

/// Holds the exact target profile's existing App startup barrier without creating privileged files.
fn acquire_target_user_installer_mutation_reader(target_sid: &str) -> Result<File, String> {
    let installer_mutation_path = query_target_user_profile_path(target_sid)?
        .join("AppData")
        .join("Local")
        .join("ClashSharp")
        .join("InstallerMutation.lock");
    validate_ordinary_directory_chain(
        installer_mutation_path
            .parent()
            .ok_or_else(|| String::from("installer.machine_helper.barrier_path_invalid"))?,
        "installer.machine_helper.barrier_path_unsafe",
    )?;
    open_installer_mutation_reader(&installer_mutation_path).map_err(|error| {
        format!("installer.machine_helper.barrier_unavailable: {target_sid}: {error}")
    })
}

/// Queries the fixed target package, distinguishing a proven absence from a query failure.
fn query_target_package_registration_if_present(
    target_sid: &str,
) -> Result<Option<TargetPackageRegistration>, String> {
    validate_owner_sid(target_sid)?;
    let command = format!(
        "$sidText = {}; \
         $sid = [Security.Principal.SecurityIdentifier]::new($sidText); \
         if ($sid.Value -cne $sidText) {{ throw 'target SID is not canonical' }}; \
         $profileKey = 'Registry::HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\' + $sidText; \
         $profilePath = [Environment]::ExpandEnvironmentVariables(\
             [string](Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath); \
         $packages = @(Get-AppxPackage -User $sidText -Name {}); \
         if ($packages.Count -eq 0) {{ [Console]::Out.Write('__ABSENT__'); exit 0 }}; \
         if ($packages.Count -ne 1) {{ throw 'target package registration is not unique' }}; \
         [PSCustomObject]@{{ \
             installLocation = [string]$packages[0].InstallLocation; \
             packageFullName = [string]$packages[0].PackageFullName; \
             packageFamilyName = [string]$packages[0].PackageFamilyName; \
             publisher = [string]$packages[0].Publisher; \
             publisherId = [string]$packages[0].PublisherId; \
             signatureKind = [string]$packages[0].SignatureKind; \
             isDevelopmentMode = [bool]$packages[0].IsDevelopmentMode; \
             profilePath = $profilePath \
         }} | ConvertTo-Json -Compress",
        powershell_quote_text(target_sid),
        powershell_quote_text(PACKAGE_IDENTITY_NAME),
    );
    let output = run_powershell_capture(&command)?;
    let text = successful_output_text(output, "installer.target_package.query_failed")?;
    if text == "__ABSENT__" {
        Ok(None)
    } else {
        TargetPackageRegistration::parse_json(&text).map(Some)
    }
}

/// Reads only the fixed ProgramData transaction file; malformed or unsafe state fails closed.
fn read_installer_transaction(path: &Path) -> InstallerTransactionState {
    let Some(directory) = path.parent() else {
        return InstallerTransactionState::Invalid;
    };
    let directory_metadata = match std::fs::symlink_metadata(directory) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return InstallerTransactionState::Missing;
        }
        Err(_) => return InstallerTransactionState::Invalid,
    };
    if !directory_metadata.is_dir() || metadata_is_reparse_point(&directory_metadata) {
        return InstallerTransactionState::Invalid;
    }
    if validate_ordinary_directory_chain(directory, "installer.transaction.path_unsafe").is_err() {
        return InstallerTransactionState::Invalid;
    }

    let metadata = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return InstallerTransactionState::Missing;
        }
        Err(_) => return InstallerTransactionState::Invalid,
    };
    if !metadata.is_file()
        || metadata_is_reparse_point(&metadata)
        || metadata.len() == 0
        || metadata.len() > MAX_INSTALLER_TRANSACTION_BYTES as u64
    {
        return InstallerTransactionState::Invalid;
    }

    match std::fs::read(path)
        .ok()
        .and_then(|bytes| InstallerTransactionJournal::parse(&bytes).ok())
    {
        Some(journal) => InstallerTransactionState::Valid(journal),
        None => InstallerTransactionState::Invalid,
    }
}

/// Atomically replaces the protected public transaction journal and verifies the exact bytes.
fn write_installer_transaction(
    common_application_data: &Path,
    journal_path: &Path,
    journal: &InstallerTransactionJournal,
) -> Result<(), String> {
    let json = journal.to_json()?;
    let script = render_installer_transaction_write_script(
        common_application_data,
        journal_path,
        journal.transaction_id(),
        &json,
    );
    let exit_code = run_powershell_stdin(&script)?;
    if exit_code != 0 {
        return Err(format!(
            "installer.transaction.persist_failed: exit code {exit_code}"
        ));
    }
    match read_installer_transaction(journal_path) {
        InstallerTransactionState::Valid(persisted) if persisted == *journal => {
            verify_installer_transaction_protection(journal_path)
        }
        _ => Err(String::from(
            "installer.transaction.persist_verification_failed",
        )),
    }
}

/// Verifies every rename-capable ancestor and the journal deny non-admin write/delete authority.
fn verify_installer_transaction_protection(journal_path: &Path) -> Result<(), String> {
    let command = render_installer_transaction_protection_script(journal_path);
    let output = run_powershell_capture(&command)?;
    if successful_output_text(output, "installer.transaction.protection_invalid")? == "True" {
        Ok(())
    } else {
        Err(String::from("installer.transaction.protection_invalid"))
    }
}

/// Renders the read-only DACL/owner verification used before trusting an existing journal.
fn render_installer_transaction_protection_script(journal_path: &Path) -> String {
    format!(
        r#"
$ErrorActionPreference = 'Stop'
$journalPath = {journal_path}
$installerRoot = Split-Path -Parent $journalPath
$productRoot = Split-Path -Parent $installerRoot
$systemSid = 'S-1-5-18'
$administratorsSid = 'S-1-5-32-544'
$writeMask = [int64]852310
foreach ($path in @($productRoot, $installerRoot, $journalPath)) {{
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
        throw "transaction protection path is a reparse point: $path"
    }}
    $acl = Get-Acl -LiteralPath $path
    if (-not $acl.AreAccessRulesProtected) {{
        throw "transaction DACL inherits writable authority: $path"
    }}
    $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -cne $systemSid -and $ownerSid -cne $administratorsSid) {{
        throw "transaction owner is not trusted: $path"
    }}
    $systemFull = $false
    $administratorsFull = $false
    foreach ($rule in $acl.Access) {{
        $sid = $rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]).Value
        $rights = [int64]$rule.FileSystemRights
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {{
            if ($sid -ceq $systemSid -and
                ($rights -band [int64][Security.AccessControl.FileSystemRights]::FullControl) -eq
                    [int64][Security.AccessControl.FileSystemRights]::FullControl) {{
                $systemFull = $true
            }}
            if ($sid -ceq $administratorsSid -and
                ($rights -band [int64][Security.AccessControl.FileSystemRights]::FullControl) -eq
                    [int64][Security.AccessControl.FileSystemRights]::FullControl) {{
                $administratorsFull = $true
            }}
            if ($sid -cne $systemSid -and $sid -cne $administratorsSid -and
                ($rights -band $writeMask) -ne 0) {{
                throw "non-administrator can mutate transaction state: $path"
            }}
        }}
    }}
    if (-not $systemFull -or -not $administratorsFull) {{
        throw "transaction administrator authority is incomplete: $path"
    }}
}}
[Console]::Out.Write('True')
"#,
        journal_path = powershell_quote(journal_path),
    )
}

/// Deletes the public release marker only after the helper has reached and rechecked Verified.
fn clear_installer_transaction(
    common_application_data: &Path,
    journal_path: &Path,
) -> Result<(), String> {
    let script = render_installer_transaction_clear_script(common_application_data, journal_path);
    let exit_code = run_powershell_stdin(&script)?;
    if exit_code != 0 {
        return Err(format!(
            "installer.transaction.clear_failed: exit code {exit_code}"
        ));
    }
    if matches!(
        read_installer_transaction(journal_path),
        InstallerTransactionState::Missing
    ) {
        Ok(())
    } else {
        Err(String::from(
            "installer.transaction.clear_verification_failed",
        ))
    }
}

/// Renders the fixed-path, write-through, same-directory transaction replacement.
fn render_installer_transaction_write_script(
    common_application_data: &Path,
    journal_path: &Path,
    transaction_id: &str,
    json: &str,
) -> String {
    format!(
        r#"
$ErrorActionPreference = 'Stop'
try {{
    $programData = {program_data}
    $productRoot = Join-Path $programData 'ClashSharp'
    $installerRoot = Join-Path $productRoot 'Installer'
    $journalPath = {journal_path}
    $transactionId = {transaction_id}
    $journalJson = {journal_json}
    $expectedPath = Join-Path $installerRoot 'transaction.json'
    if (-not ([IO.Path]::GetFullPath($journalPath)).Equals(
            [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase) -or
        $transactionId -cnotmatch '^[0-9a-f]{{64}}$' -or
        [Text.Encoding]::UTF8.GetByteCount($journalJson) -gt {maximum_bytes}) {{
        throw 'transaction inputs are not canonical'
    }}
    foreach ($path in @($programData, $productRoot, $installerRoot)) {{
        if (Test-Path -LiteralPath $path) {{
            $item = Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
                throw "unsafe transaction directory: $path"
            }}
        }}
    }}
    [IO.Directory]::CreateDirectory($productRoot) | Out-Null
    [IO.Directory]::CreateDirectory($installerRoot) | Out-Null
    foreach ($path in @($productRoot, $installerRoot)) {{
        $item = Get-Item -LiteralPath $path -Force
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
            throw "unsafe created transaction directory: $path"
        }}
    }}
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $usersSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $productAcl = [Security.AccessControl.DirectorySecurity]::new()
    $productAcl.SetAccessRuleProtection($true, $false)
    $productAcl.SetOwner($administratorsSid)
    foreach ($sid in @($systemSid, $administratorsSid)) {{
        $productAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $allow))
    }}
    $productAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $usersSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $allow))
    Set-Acl -LiteralPath $productRoot -AclObject $productAcl
    $directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
    $directoryAcl.SetAccessRuleProtection($true, $false)
    $directoryAcl.SetOwner($administratorsSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $none = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @($systemSid, $administratorsSid)) {{
        $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $none, $allow))
    }}
    $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $usersSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance, $none, $allow))
    Set-Acl -LiteralPath $installerRoot -AclObject $directoryAcl
    foreach ($orphan in @(Get-ChildItem -LiteralPath $installerRoot -File -Force)) {{
        if ($orphan.Name -cnotmatch '^\.transaction-[0-9a-f]{{64}}\.tmp$') {{ continue }}
        if (($orphan.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
            throw 'orphan transaction temporary file is a reparse point'
        }}
        Remove-Item -LiteralPath $orphan.FullName -Force
    }}
    $tempPath = Join-Path $installerRoot ('.transaction-' + $transactionId + '.tmp')
    if (Test-Path -LiteralPath $tempPath) {{
        $tempItem = Get-Item -LiteralPath $tempPath -Force
        if ($tempItem.PSIsContainer -or
            ($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
            throw 'transaction temporary path is unsafe'
        }}
        Remove-Item -LiteralPath $tempPath -Force
    }}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($journalJson)
    $stream = [IO.FileStream]::new(
        $tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {{
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }} finally {{ $stream.Dispose() }}
    $fileAcl = [Security.AccessControl.FileSecurity]::new()
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.SetOwner($administratorsSid)
    foreach ($sid in @($systemSid, $administratorsSid)) {{
        $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $allow))
    }}
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $usersSid, [Security.AccessControl.FileSystemRights]::Read, $allow))
    Set-Acl -LiteralPath $tempPath -AclObject $fileAcl
    if (Test-Path -LiteralPath $journalPath) {{
        $existing = Get-Item -LiteralPath $journalPath -Force
        if ($existing.PSIsContainer -or
            ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
            throw 'existing transaction journal is unsafe'
        }}
        [IO.File]::Replace($tempPath, $journalPath, $null, $true)
    }} else {{
        Move-Item -LiteralPath $tempPath -Destination $journalPath
    }}
    Set-Acl -LiteralPath $journalPath -AclObject $fileAcl
    $verify = [IO.File]::Open(
        $journalPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {{
        $reader = [IO.StreamReader]::new($verify, [Text.UTF8Encoding]::new($false), $true)
        try {{ $actual = $reader.ReadToEnd() }} finally {{ $reader.Dispose() }}
    }} finally {{ if ($null -ne $verify) {{ $verify.Dispose() }} }}
    if (-not $actual.Equals($journalJson, [StringComparison]::Ordinal)) {{
        throw 'transaction journal verification failed'
    }}
    exit 0
}} catch {{
    [Console]::Error.Write('installer.transaction.persist_failed: ' + $_.Exception.Message)
    exit 1
}}
"#,
        program_data = powershell_quote(common_application_data),
        journal_path = powershell_quote(journal_path),
        transaction_id = powershell_quote_text(transaction_id),
        journal_json = powershell_quote_text(json),
        maximum_bytes = MAX_INSTALLER_TRANSACTION_BYTES,
    )
}

/// Renders deletion of only the fixed, non-reparse public journal.
fn render_installer_transaction_clear_script(
    common_application_data: &Path,
    journal_path: &Path,
) -> String {
    format!(
        r#"
$ErrorActionPreference = 'Stop'
try {{
    $programData = {program_data}
    $installerRoot = Join-Path (Join-Path $programData 'ClashSharp') 'Installer'
    $journalPath = {journal_path}
    $expectedPath = Join-Path $installerRoot 'transaction.json'
    if (-not ([IO.Path]::GetFullPath($journalPath)).Equals(
            [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase)) {{
        throw 'transaction path escaped fixed root'
    }}
    if (-not (Test-Path -LiteralPath $journalPath)) {{ exit 0 }}
    foreach ($path in @($installerRoot, $journalPath)) {{
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
            throw "transaction path is a reparse point: $path"
        }}
    }}
    $journal = Get-Item -LiteralPath $journalPath -Force
    if ($journal.PSIsContainer -or $journal.Length -lt 1 -or
        $journal.Length -gt {maximum_bytes}) {{
        throw 'transaction journal is unsafe'
    }}
    Remove-Item -LiteralPath $journalPath -Force
    if (Test-Path -LiteralPath $journalPath) {{ throw 'transaction journal deletion failed' }}
    exit 0
}} catch {{
    [Console]::Error.Write('installer.transaction.clear_failed: ' + $_.Exception.Message)
    exit 1
}}
"#,
        program_data = powershell_quote(common_application_data),
        journal_path = powershell_quote(journal_path),
        maximum_bytes = MAX_INSTALLER_TRANSACTION_BYTES,
    )
}

/// Reads only the fixed association and treats unsafe/malformed state as invalid, not as ownership.
fn read_machine_association(resources: &MachineResourcePlan) -> AssociationState {
    let association_path = resources.association_path();
    let service_root_metadata = match std::fs::symlink_metadata(resources.service_data_root()) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return AssociationState::Missing;
        }
        Err(_) => return AssociationState::Invalid,
    };
    if !service_root_metadata.is_dir() || metadata_is_reparse_point(&service_root_metadata) {
        return AssociationState::Invalid;
    }

    let metadata = match std::fs::symlink_metadata(association_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return AssociationState::Missing;
        }
        Err(_) => return AssociationState::Invalid,
    };
    if !metadata.is_file()
        || metadata_is_reparse_point(&metadata)
        || metadata.len() == 0
        || metadata.len() > 4096
    {
        return AssociationState::Invalid;
    }
    match std::fs::read(association_path)
        .ok()
        .and_then(|bytes| MachineAssociation::parse(&bytes).ok())
    {
        Some(association) => AssociationState::Valid(association),
        None => AssociationState::Invalid,
    }
}

/// Queries only the fixed service name.
fn query_mihomo_service_exists() -> Result<bool, String> {
    let command = format!(
        "$service = Get-Service -Name {} -ErrorAction SilentlyContinue; \
         [Console]::Out.Write(($null -ne $service).ToString())",
        powershell_quote_text(SERVICE_NAME),
    );
    let output = run_powershell_capture(&command)?;
    match successful_output_text(output, "installer.machine.service_query_failed")?.as_str() {
        "True" => Ok(true),
        "False" => Ok(false),
        _ => Err(String::from("installer.machine.service_query_invalid")),
    }
}

/// Requires the fixed service to exist and report the Running state after commit.
fn query_mihomo_service_is_running() -> Result<bool, String> {
    let command = format!(
        "$service = Get-Service -Name {} -ErrorAction SilentlyContinue; \
         [Console]::Out.Write(($null -ne $service -and $service.Status -eq \
             [System.ServiceProcess.ServiceControllerStatus]::Running).ToString())",
        powershell_quote_text(SERVICE_NAME),
    );
    let output = run_powershell_capture(&command)?;
    match successful_output_text(output, "installer.machine.service_query_failed")?.as_str() {
        "True" => Ok(true),
        "False" => Ok(false),
        _ => Err(String::from("installer.machine.service_query_invalid")),
    }
}

/// Rejects missing, non-directory, or reparse-point components in an existing absolute chain.
fn validate_ordinary_directory_chain(directory: &Path, error_code: &str) -> Result<(), String> {
    if !directory.is_absolute() {
        return Err(format!("{error_code}: path is not absolute"));
    }
    let mut ancestors = directory.ancestors().collect::<Vec<_>>();
    ancestors.reverse();
    for ancestor in ancestors {
        if ancestor.as_os_str().is_empty() {
            continue;
        }
        let metadata = std::fs::symlink_metadata(ancestor)
            .map_err(|error| format!("{error_code}: {error}"))?;
        if !metadata.is_dir() || metadata_is_reparse_point(&metadata) {
            return Err(format!(
                "{error_code}: unsafe component {}",
                ancestor.display()
            ));
        }
    }
    Ok(())
}

/// Returns whether a filesystem entry carries the Windows reparse-point attribute.
fn metadata_is_reparse_point(metadata: &std::fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
        metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

/// Resolves automatic language selection to a concrete language.
fn resolve_language(language: InstallerLanguage) -> InstallerLanguage {
    match language {
        InstallerLanguage::AutoDetect => detect_system_language(),
        _ => language,
    }
}

/// Returns the compact language label shown on the top-right selector.
fn language_short_label(language: InstallerLanguage) -> &'static str {
    match resolve_language(language) {
        InstallerLanguage::SimplifiedChinese => "中",
        InstallerLanguage::TraditionalChinese => "繁",
        InstallerLanguage::English => "EN",
        InstallerLanguage::Russian => "RU",
        InstallerLanguage::French => "FR",
        InstallerLanguage::German => "DE",
        InstallerLanguage::AutoDetect => "EN",
    }
}

/// Detects the Windows UI culture and maps it to the Clash# language set.
fn detect_system_language() -> InstallerLanguage {
    let culture =
        run_powershell_capture("[System.Globalization.CultureInfo]::CurrentUICulture.Name")
            .ok()
            .and_then(|output| String::from_utf8(output.stdout).ok())
            .or_else(|| std::env::var("LANG").ok())
            .unwrap_or_default()
            .to_ascii_lowercase();

    if culture.starts_with("zh-hant")
        || culture.starts_with("zh-tw")
        || culture.starts_with("zh-hk")
        || culture.starts_with("zh-mo")
    {
        InstallerLanguage::TraditionalChinese
    } else if culture.starts_with("zh") {
        InstallerLanguage::SimplifiedChinese
    } else if culture.starts_with("ru") {
        InstallerLanguage::Russian
    } else if culture.starts_with("fr") {
        InstallerLanguage::French
    } else if culture.starts_with("de") {
        InstallerLanguage::German
    } else {
        InstallerLanguage::SimplifiedChinese
    }
}

/// Reads the Windows app-theme preference.
fn detect_system_dark_theme() -> bool {
    hidden_command("reg")
        .args([
            "query",
            r"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "/v",
            "AppsUseLightTheme",
        ])
        .output()
        .ok()
        .and_then(|output| {
            if output.status.success() {
                String::from_utf8(output.stdout).ok()
            } else {
                None
            }
        })
        .and_then(|value| parse_registry_dword_output(&value))
        .map(|value| value == 0)
        .unwrap_or(false)
}

/// Parses a `REG_DWORD` value from `reg query` output.
fn parse_registry_dword_output(output: &str) -> Option<u32> {
    output.split_whitespace().rev().find_map(|part| {
        if let Some(hex) = part.strip_prefix("0x") {
            u32::from_str_radix(hex, 16).ok()
        } else {
            part.parse::<u32>().ok()
        }
    })
}

/// Returns all localized UI strings for a concrete language.
fn localized_text(language: InstallerLanguage) -> TextPack {
    match language {
        InstallerLanguage::AutoDetect | InstallerLanguage::SimplifiedChinese => TextPack {
            window_title: "Clash# 安装程序",
            product_title: "Clash#",
            product_subtitle: "Windows 原生 Clash 代理工具",
            checking_title: "正在识别当前环境",
            checking_message: "正在检查 Windows 版本、安装状态和安装包内容。",
            unsupported_title: "无法安装 Clash#。",
            unsupported_message: "此设备不满足 Clash# 的安装要求。",
            missing_payload_title: "安装包不完整。",
            missing_payload_message: "未找到所需的 MSIX 包或证书文件。",
            not_installed_title: "你尚未安装 Clash#。",
            not_installed_message: "安装程序将先安装证书，然后安装 Clash# MSIX 包。",
            installed_title: "Clash# 已经安装。",
            installed_message: "选择你需要执行的操作。",
            install_button: "安装",
            repair_button: "修补",
            uninstall_button: "卸载",
            refresh_button: "刷新",
            admin_hint: "应用安装保持当前用户上下文；配置系统服务时才会请求管理员确认",
            preparing_install: "正在准备安装",
            preparing_repair: "正在准备修补",
            preparing_uninstall: "正在准备卸载",
            certificate_title: "正在安装证书",
            certificate_message: "正在导入 Clash# 包证书。",
            removing_title: "正在处理系统组件",
            removing_message: "正在安全处理 Clash# 本地服务和现有安装。",
            package_title: "正在安装 MSIX",
            package_message: "正在部署 Clash# 应用包和运行时依赖。",
            uninstall_title: "正在卸载 Clash#",
            uninstall_message: "正在从当前用户移除 Clash# MSIX 包。",
            installed_done: "Clash# 已安装。",
            repaired_done: "Clash# 已修补。",
            uninstalled_done: "Clash# 已卸载。",
            failed_title: "操作未完成。",
            details_title: "运行信息",
            close_button: "关闭",
            language_auto: "自动检测",
            theme_follow_system: "跟随系统",
            theme_light: "浅色",
            theme_dark: "深色",
            system_supported: "系统: Windows 11 x64",
            system_unsupported: "系统: 不受支持",
            installed_yes: "已安装: 是",
            installed_no: "已安装: 否",
            package_missing: "包: 未找到",
            certificate_missing: "证书: 未找到",
            dependencies_none: "依赖: 无",
            dependencies_prefix: "依赖",
            version_label: "版本",
            license_label: "协议",
        },
        InstallerLanguage::TraditionalChinese => TextPack {
            window_title: "Clash# 安裝程式",
            product_title: "Clash#",
            product_subtitle: "Windows 原生 Clash 代理工具",
            checking_title: "正在識別目前環境",
            checking_message: "正在檢查 Windows 版本、安裝狀態與安裝包內容。",
            unsupported_title: "無法安裝 Clash#。",
            unsupported_message: "此裝置不符合 Clash# 的安裝需求。",
            missing_payload_title: "安裝包不完整。",
            missing_payload_message: "找不到必要的 MSIX 包或憑證檔案。",
            not_installed_title: "你尚未安裝 Clash#。",
            not_installed_message: "安裝程式會先安裝憑證，然後安裝 Clash# MSIX 包。",
            installed_title: "Clash# 已經安裝。",
            installed_message: "選擇你需要執行的操作。",
            install_button: "安裝",
            repair_button: "修補",
            uninstall_button: "解除安裝",
            refresh_button: "重新整理",
            admin_hint: "應用安裝保持目前使用者內容；設定系統服務時才會要求管理員確認",
            preparing_install: "正在準備安裝",
            preparing_repair: "正在準備修補",
            preparing_uninstall: "正在準備解除安裝",
            certificate_title: "正在安裝憑證",
            certificate_message: "正在匯入 Clash# 包憑證。",
            removing_title: "正在處理系統元件",
            removing_message: "正在安全處理 Clash# 本機服務與既有安裝。",
            package_title: "正在安裝 MSIX",
            package_message: "正在部署 Clash# 應用包與執行階段相依項。",
            uninstall_title: "正在解除安裝 Clash#",
            uninstall_message: "正在從目前使用者移除 Clash# MSIX 包。",
            installed_done: "Clash# 已安裝。",
            repaired_done: "Clash# 已修補。",
            uninstalled_done: "Clash# 已解除安裝。",
            failed_title: "操作未完成。",
            details_title: "執行資訊",
            close_button: "關閉",
            language_auto: "自动检测",
            theme_follow_system: "跟隨系統",
            theme_light: "淺色",
            theme_dark: "深色",
            system_supported: "系統: Windows 11 x64",
            system_unsupported: "系統: 不支援",
            installed_yes: "已安裝: 是",
            installed_no: "已安裝: 否",
            package_missing: "包: 未找到",
            certificate_missing: "憑證: 未找到",
            dependencies_none: "相依項: 無",
            dependencies_prefix: "相依項",
            version_label: "版本",
            license_label: "授權",
        },
        InstallerLanguage::English => TextPack {
            window_title: "Clash# Installer",
            product_title: "Clash#",
            product_subtitle: "Windows-native Clash proxy client",
            checking_title: "Checking this device",
            checking_message: "Checking Windows version, installation state, and package payload.",
            unsupported_title: "Clash# cannot be installed.",
            unsupported_message: "This device does not meet the Clash# installation requirements.",
            missing_payload_title: "Installation payload is incomplete.",
            missing_payload_message: "The required MSIX package or certificate file was not found.",
            not_installed_title: "Clash# is not installed.",
            not_installed_message: "Setup will install the certificate first, then install the Clash# MSIX package.",
            installed_title: "Clash# is already installed.",
            installed_message: "Choose the action you want to run.",
            install_button: "Install",
            repair_button: "Repair",
            uninstall_button: "Uninstall",
            refresh_button: "Refresh",
            admin_hint: "App setup stays in your user context; only system-service setup requests administrator confirmation",
            preparing_install: "Preparing installation",
            preparing_repair: "Preparing repair",
            preparing_uninstall: "Preparing uninstall",
            certificate_title: "Installing certificate",
            certificate_message: "Importing the Clash# package certificate.",
            removing_title: "Handling system components",
            removing_message: "Safely handling the Clash# local service and existing installation.",
            package_title: "Installing MSIX",
            package_message: "Deploying the Clash# app package and runtime dependencies.",
            uninstall_title: "Uninstalling Clash#",
            uninstall_message: "Removing the Clash# MSIX package for the current user.",
            installed_done: "Clash# has been installed.",
            repaired_done: "Clash# has been repaired.",
            uninstalled_done: "Clash# has been uninstalled.",
            failed_title: "The operation did not complete.",
            details_title: "Runtime Information",
            close_button: "Close",
            language_auto: "自动检测",
            theme_follow_system: "Follow system",
            theme_light: "Light",
            theme_dark: "Dark",
            system_supported: "System: Windows 11 x64",
            system_unsupported: "System: unsupported",
            installed_yes: "Installed: yes",
            installed_no: "Installed: no",
            package_missing: "Package: not found",
            certificate_missing: "Certificate: not found",
            dependencies_none: "Dependencies: none",
            dependencies_prefix: "Dependencies",
            version_label: "Version",
            license_label: "License",
        },
        InstallerLanguage::Russian => TextPack {
            window_title: "Установщик Clash#",
            product_title: "Clash#",
            product_subtitle: "Нативный Clash-прокси для Windows",
            checking_title: "Проверка устройства",
            checking_message: "Проверяется версия Windows, состояние установки и пакет.",
            unsupported_title: "Clash# нельзя установить.",
            unsupported_message: "Это устройство не соответствует требованиям Clash#.",
            missing_payload_title: "Пакет установки неполный.",
            missing_payload_message: "Не найден пакет MSIX или файл сертификата.",
            not_installed_title: "Clash# не установлен.",
            not_installed_message: "Сначала будет установлен сертификат, затем пакет Clash# MSIX.",
            installed_title: "Clash# уже установлен.",
            installed_message: "Выберите действие.",
            install_button: "Установить",
            repair_button: "Исправить",
            uninstall_button: "Удалить",
            refresh_button: "Обновить",
            admin_hint: "Приложение устанавливается для текущего пользователя; подтверждение администратора требуется только для службы",
            preparing_install: "Подготовка установки",
            preparing_repair: "Подготовка исправления",
            preparing_uninstall: "Подготовка удаления",
            certificate_title: "Установка сертификата",
            certificate_message: "Импортируется сертификат пакета Clash#.",
            removing_title: "Обработка системных компонентов",
            removing_message: "Безопасная обработка локальной службы и существующей установки Clash#.",
            package_title: "Установка MSIX",
            package_message: "Развертывается приложение Clash# и зависимости.",
            uninstall_title: "Удаление Clash#",
            uninstall_message: "Удаляется пакет Clash# MSIX для текущего пользователя.",
            installed_done: "Clash# установлен.",
            repaired_done: "Clash# исправлен.",
            uninstalled_done: "Clash# удален.",
            failed_title: "Операция не завершена.",
            details_title: "Сведения о запуске",
            close_button: "Закрыть",
            language_auto: "自动检测",
            theme_follow_system: "Система",
            theme_light: "Светлая",
            theme_dark: "Темная",
            system_supported: "Система: Windows 11 x64",
            system_unsupported: "Система: не поддерживается",
            installed_yes: "Установлено: да",
            installed_no: "Установлено: нет",
            package_missing: "Пакет: не найден",
            certificate_missing: "Сертификат: не найден",
            dependencies_none: "Зависимости: нет",
            dependencies_prefix: "Зависимости",
            version_label: "Версия",
            license_label: "Лицензия",
        },
        InstallerLanguage::French => TextPack {
            window_title: "Programme d'installation Clash#",
            product_title: "Clash#",
            product_subtitle: "Client proxy Clash natif pour Windows",
            checking_title: "Verification de cet appareil",
            checking_message: "Verification de la version Windows, de l'installation et du paquet.",
            unsupported_title: "Clash# ne peut pas etre installe.",
            unsupported_message: "Cet appareil ne respecte pas les exigences de Clash#.",
            missing_payload_title: "Paquet d'installation incomplet.",
            missing_payload_message: "Le paquet MSIX ou le certificat requis est introuvable.",
            not_installed_title: "Clash# n'est pas installe.",
            not_installed_message: "Le certificat sera installe avant le paquet Clash# MSIX.",
            installed_title: "Clash# est deja installe.",
            installed_message: "Choisissez l'action a executer.",
            install_button: "Installer",
            repair_button: "Reparer",
            uninstall_button: "Desinstaller",
            refresh_button: "Actualiser",
            admin_hint: "L'application reste dans le contexte utilisateur; seule la configuration du service demande une confirmation administrateur",
            preparing_install: "Preparation de l'installation",
            preparing_repair: "Preparation de la reparation",
            preparing_uninstall: "Preparation de la desinstallation",
            certificate_title: "Installation du certificat",
            certificate_message: "Importation du certificat du paquet Clash#.",
            removing_title: "Traitement des composants systeme",
            removing_message: "Traitement securise du service local et de l'installation Clash# existante.",
            package_title: "Installation MSIX",
            package_message: "Deploiement de l'application Clash# et de ses dependances.",
            uninstall_title: "Desinstallation de Clash#",
            uninstall_message: "Suppression du paquet Clash# MSIX pour l'utilisateur actuel.",
            installed_done: "Clash# est installe.",
            repaired_done: "Clash# est repare.",
            uninstalled_done: "Clash# est desinstalle.",
            failed_title: "L'operation n'est pas terminee.",
            details_title: "Informations d'execution",
            close_button: "Fermer",
            language_auto: "自动检测",
            theme_follow_system: "Systeme",
            theme_light: "Clair",
            theme_dark: "Sombre",
            system_supported: "Systeme: Windows 11 x64",
            system_unsupported: "Systeme: non pris en charge",
            installed_yes: "Installe: oui",
            installed_no: "Installe: non",
            package_missing: "Paquet: introuvable",
            certificate_missing: "Certificat: introuvable",
            dependencies_none: "Dependances: aucune",
            dependencies_prefix: "Dependances",
            version_label: "Version",
            license_label: "Licence",
        },
        InstallerLanguage::German => TextPack {
            window_title: "Clash# Installer",
            product_title: "Clash#",
            product_subtitle: "Windows-nativer Clash-Proxyclient",
            checking_title: "Dieses Geraet wird geprueft",
            checking_message: "Windows-Version, Installationsstatus und Paket werden geprueft.",
            unsupported_title: "Clash# kann nicht installiert werden.",
            unsupported_message: "Dieses Geraet erfuellt die Anforderungen von Clash# nicht.",
            missing_payload_title: "Installationspaket unvollstaendig.",
            missing_payload_message: "Das erforderliche MSIX-Paket oder Zertifikat wurde nicht gefunden.",
            not_installed_title: "Clash# ist nicht installiert.",
            not_installed_message: "Zuerst wird das Zertifikat installiert, danach das Clash# MSIX-Paket.",
            installed_title: "Clash# ist bereits installiert.",
            installed_message: "Waehlen Sie die auszufuehrende Aktion.",
            install_button: "Installieren",
            repair_button: "Reparieren",
            uninstall_button: "Deinstallieren",
            refresh_button: "Aktualisieren",
            admin_hint: "Die App bleibt im Benutzerkontext; nur der Systemdienst erfordert eine Administratorbestaetigung",
            preparing_install: "Installation wird vorbereitet",
            preparing_repair: "Reparatur wird vorbereitet",
            preparing_uninstall: "Deinstallation wird vorbereitet",
            certificate_title: "Zertifikat wird installiert",
            certificate_message: "Das Clash#-Paketzertifikat wird importiert.",
            removing_title: "Systemkomponenten werden verarbeitet",
            removing_message: "Lokaler Clash#-Dienst und vorhandene Installation werden sicher verarbeitet.",
            package_title: "MSIX wird installiert",
            package_message: "Clash# und Laufzeitabhaengigkeiten werden bereitgestellt.",
            uninstall_title: "Clash# wird deinstalliert",
            uninstall_message: "Das Clash# MSIX-Paket wird fuer den aktuellen Benutzer entfernt.",
            installed_done: "Clash# wurde installiert.",
            repaired_done: "Clash# wurde repariert.",
            uninstalled_done: "Clash# wurde deinstalliert.",
            failed_title: "Der Vorgang wurde nicht abgeschlossen.",
            details_title: "Laufzeitinformationen",
            close_button: "Schliessen",
            language_auto: "自动检测",
            theme_follow_system: "System",
            theme_light: "Hell",
            theme_dark: "Dunkel",
            system_supported: "System: Windows 11 x64",
            system_unsupported: "System: nicht unterstuetzt",
            installed_yes: "Installiert: ja",
            installed_no: "Installiert: nein",
            package_missing: "Paket: nicht gefunden",
            certificate_missing: "Zertifikat: nicht gefunden",
            dependencies_none: "Abhaengigkeiten: keine",
            dependencies_prefix: "Abhaengigkeiten",
            version_label: "Version",
            license_label: "Lizenz",
        },
    }
}

/// Formats the runtime information shown in the details dialog.
fn format_environment_details(state: &EnvironmentState, text: TextPack) -> String {
    let version = resolve_clashsharp_version(state.context.as_ref().ok());
    let product = format!(
        "ClashSharp {}: {}\n{}: {}",
        text.version_label, version, text.license_label, CLASHSHARP_LICENSE
    );

    let system = match state.support.as_ref() {
        Ok(info) => format!(
            "{}\nBuild: {}\nArchitecture: {}",
            text.system_supported, info.build, info.architecture
        ),
        Err(error) => format!("{}\n{}", text.system_unsupported, error),
    };

    let installed = if state.is_installed {
        text.installed_yes
    } else {
        text.installed_no
    };

    let payload = match state.context.as_ref() {
        Ok(context) => {
            let package = file_name_or(context.package_path.as_ref(), text.package_missing);
            let certificate =
                file_name_or(context.certificate_path.as_ref(), text.certificate_missing);
            let dependencies = if context.dependency_paths.is_empty() {
                text.dependencies_none.to_owned()
            } else {
                format!(
                    "{}: {}",
                    text.dependencies_prefix,
                    context.dependency_paths.len()
                )
            };

            format!(
                "Payload: {}\nPackage: {package}\nCertificate: {certificate}\n{dependencies}",
                compact_path(&context.payload_dir)
            )
        }
        Err(error) => format!("Payload: {error}"),
    };

    format!("{product}\n{system}\n{installed}\n{payload}")
}

/// Resolves the Clash# package version from the payload name or project manifest.
fn resolve_clashsharp_version(context: Option<&InstallerContext>) -> String {
    context
        .and_then(|context| context.package_path.as_ref())
        .and_then(|path| parse_version_from_package_name(path))
        .or_else(find_manifest_version)
        .unwrap_or_else(|| String::from("unknown"))
}

/// Finds the main app manifest from nearby repository ancestors and reads its package version.
fn find_manifest_version() -> Option<String> {
    let exe_dir = std::env::current_exe().ok()?.parent()?.to_path_buf();

    for ancestor in exe_dir.ancestors() {
        let manifest = ancestor.join("ClashSharp").join("Package.appxmanifest");
        if let Some(version) = read_manifest_version(&manifest) {
            return Some(version);
        }
    }

    None
}

/// Reads the package identity version from an Appx manifest file.
fn read_manifest_version(path: &Path) -> Option<String> {
    let text = std::fs::read_to_string(path).ok()?;
    read_manifest_version_text(&text)
}

/// Returns a path's file name or a localized fallback label.
fn file_name_or(path: Option<&PathBuf>, fallback: &'static str) -> String {
    path.and_then(|path| path.file_name())
        .and_then(|name| name.to_str())
        .map(String::from)
        .unwrap_or_else(|| fallback.to_owned())
}

/// Runs a CurrentUser mutation only after the child owns its own startup-barrier reader.
fn run_parent_mutating_powershell(command: &str, barrier_path: &Path) -> Result<(), String> {
    let exit_code = run_powershell_stdin(&render_parent_mutating_powershell_command(
        command,
        barrier_path,
    ))?;
    if exit_code == 0 {
        Ok(())
    } else {
        Err(format!(
            "installer.powershell.command_failed: exit code {exit_code}"
        ))
    }
}

fn render_parent_mutating_powershell_command(command: &str, barrier_path: &Path) -> String {
    format!(
        "$ErrorActionPreference = 'Stop'; \
         $barrierPath = {}; \
         $barrier = [IO.File]::Open($barrierPath, [IO.FileMode]::Open, \
             [IO.FileAccess]::Read, [IO.FileShare]::Read); \
         try {{ \
             $barrierItem = Get-Item -LiteralPath $barrierPath -Force; \
             if ($barrierItem.PSIsContainer -or \
                 ($barrierItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ \
                 throw 'installer.app.lock_file_unsafe' \
             }}; \
             & {{ {command} }} \
         }} finally {{ \
             $barrier.Dispose() \
         }}",
        powershell_quote(barrier_path),
    )
}

#[cfg(windows)]
struct KillOnCloseJob {
    handle: *mut std::ffi::c_void,
}

#[cfg(windows)]
impl KillOnCloseJob {
    fn create() -> Result<Self, String> {
        const JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS: u32 = 9;
        const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: u32 = 0x0000_2000;

        #[repr(C)]
        #[derive(Default)]
        struct BasicLimitInformation {
            per_process_user_time_limit: i64,
            per_job_user_time_limit: i64,
            limit_flags: u32,
            minimum_working_set_size: usize,
            maximum_working_set_size: usize,
            active_process_limit: u32,
            affinity: usize,
            priority_class: u32,
            scheduling_class: u32,
        }

        #[repr(C)]
        #[derive(Default)]
        struct IoCounters {
            read_operation_count: u64,
            write_operation_count: u64,
            other_operation_count: u64,
            read_transfer_count: u64,
            write_transfer_count: u64,
            other_transfer_count: u64,
        }

        #[repr(C)]
        #[derive(Default)]
        struct ExtendedLimitInformation {
            basic_limit_information: BasicLimitInformation,
            io_info: IoCounters,
            process_memory_limit: usize,
            job_memory_limit: usize,
            peak_process_memory_used: usize,
            peak_job_memory_used: usize,
        }

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateJobObjectW(
                job_attributes: *mut std::ffi::c_void,
                name: *const u16,
            ) -> *mut std::ffi::c_void;
            fn SetInformationJobObject(
                job: *mut std::ffi::c_void,
                information_class: u32,
                information: *const std::ffi::c_void,
                information_length: u32,
            ) -> i32;
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: null attributes/name request a private non-inheritable Job object.
        let handle = unsafe { CreateJobObjectW(std::ptr::null_mut(), std::ptr::null()) };
        if handle.is_null() {
            return Err(format!(
                "installer.powershell.job_create_failed: {}",
                std::io::Error::last_os_error()
            ));
        }
        let mut limits = ExtendedLimitInformation::default();
        limits.basic_limit_information.limit_flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        // SAFETY: limits has the exact documented layout and remains live for this call.
        let configured = unsafe {
            SetInformationJobObject(
                handle,
                JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS,
                std::ptr::from_ref(&limits).cast(),
                std::mem::size_of::<ExtendedLimitInformation>() as u32,
            )
        };
        if configured == 0 {
            let error = std::io::Error::last_os_error();
            // SAFETY: handle is live and not owned by a guard yet.
            let _ = unsafe { CloseHandle(handle) };
            return Err(format!(
                "installer.powershell.job_configure_failed: {error}"
            ));
        }
        Ok(Self { handle })
    }

    fn assign(&self, child: &std::process::Child) -> Result<(), String> {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn AssignProcessToJobObject(
                job: *mut std::ffi::c_void,
                process: *mut std::ffi::c_void,
            ) -> i32;
        }

        // SAFETY: both handles are live; assignment occurs before any script bytes are written.
        if unsafe { AssignProcessToJobObject(self.handle, child.as_raw_handle()) } == 0 {
            return Err(format!(
                "installer.powershell.job_assign_failed: {}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(())
    }
}

#[cfg(windows)]
impl Drop for KillOnCloseJob {
    fn drop(&mut self) {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: this guard owns the only local Job handle. KILL_ON_JOB_CLOSE intentionally
        // terminates any still-running Installer script during parent/helper teardown or crash.
        let _ = unsafe { CloseHandle(self.handle) };
    }
}

#[cfg(not(windows))]
struct KillOnCloseJob;

#[cfg(not(windows))]
impl KillOnCloseJob {
    fn create() -> Result<Self, String> {
        Ok(Self)
    }

    fn assign(&self, _child: &std::process::Child) -> Result<(), String> {
        Ok(())
    }
}

/// Executes an Installer-owned script in a kill-on-close Job through UTF-8 stdin.
fn run_powershell_stdin(script: &str) -> Result<i32, String> {
    const STDIN_BOOTSTRAP: &str = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); \
         $source=[Console]::In.ReadToEnd(); & ([ScriptBlock]::Create($source))";

    let job = KillOnCloseJob::create()?;
    let mut process = powershell_process()?;
    process
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            STDIN_BOOTSTRAP,
        ]);
    let mut child = process
        .spawn()
        .map_err(|error| format!("PowerShell failed to start: {error}"))?;
    if let Err(error) = job.assign(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    let Some(mut stdin) = child.stdin.take() else {
        let _ = child.kill();
        let _ = child.wait();
        return Err(String::from("installer.powershell.stdin_unavailable"));
    };
    if let Err(error) = stdin.write_all(script.as_bytes()) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(format!("installer.powershell.stdin_failed: {error}"));
    }
    drop(stdin);
    let output = child
        .wait_with_output()
        .map_err(|error| format!("PowerShell wait failed: {error}"))?;
    match output.status.code() {
        Some(APP_RUNNING_EXIT_CODE) => return Ok(APP_RUNNING_EXIT_CODE),
        Some(MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE) => {
            return Ok(MACHINE_SERVICE_DELETE_PENDING_REBOOT_EXIT_CODE);
        }
        _ => {}
    }

    successful_output_text(output, "installer.powershell.stdin_script_failed")?;
    Ok(0)
}

/// Returns trimmed stdout for a successful process or a stable error prefix plus bounded details.
fn successful_output_text(
    output: std::process::Output,
    failure_code: &str,
) -> Result<String, String> {
    if output.status.success() {
        return Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned());
    }

    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    let details = if stderr.is_empty() { stdout } else { stderr };
    Err(if details.is_empty() {
        format!("{failure_code}: exit code {:?}", output.status.code())
    } else {
        format!("{failure_code}: {details}")
    })
}

/// Runs a PowerShell command and returns the raw process output.
fn run_powershell_capture(command: &str) -> Result<std::process::Output, String> {
    let mut process = powershell_process()?;
    process.args([
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        command,
    ]);
    process
        .output()
        .map_err(|error| format!("PowerShell failed to start: {error}"))
}

/// Creates a minimally inherited absolute-System32 Windows PowerShell process.
fn powershell_process() -> Result<Command, String> {
    let system_directory = trusted_system_directory()?;
    let windows_directory = system_directory
        .parent()
        .ok_or_else(|| String::from("installer.system_directory.invalid"))?;
    let powershell = system_directory.join(r"WindowsPowerShell\v1.0\powershell.exe");
    let mut process = hidden_command(&powershell);
    process
        .env_clear()
        .env("SystemRoot", windows_directory)
        .env("WINDIR", windows_directory)
        .env("ComSpec", system_directory.join("cmd.exe"))
        .env("PATH", &system_directory)
        .env(
            "PSModulePath",
            system_directory.join(r"WindowsPowerShell\v1.0\Modules"),
        )
        .env("PATHEXT", ".COM;.EXE;.BAT;.CMD");
    Ok(process)
}

/// Creates a command configured to avoid flashing a console window on Windows.
fn hidden_command(program: impl AsRef<std::ffi::OsStr>) -> Command {
    let mut command = Command::new(program);
    #[cfg(windows)]
    {
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command
}

/// Resolves System32 through the Windows API instead of inherited environment variables.
fn trusted_system_directory() -> Result<PathBuf, String> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStringExt;

        unsafe extern "system" {
            fn GetSystemDirectoryW(buffer: *mut u16, size: u32) -> u32;
        }

        let mut buffer = vec![0_u16; 32_768];
        // SAFETY: the writable buffer has the exact size passed to the Win32 API.
        let length = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), buffer.len() as u32) };
        if length == 0 || length as usize >= buffer.len() {
            return Err(String::from("installer.system_directory.unavailable"));
        }
        buffer.truncate(length as usize);
        Ok(PathBuf::from(std::ffi::OsString::from_wide(&buffer)))
    }
    #[cfg(not(windows))]
    {
        Ok(PathBuf::from("powershell.exe"))
    }
}

/// Finds the first payload file matching one of the provided extensions.
fn find_payload_file(payload_dir: &Path, extensions: &[&str]) -> Option<PathBuf> {
    let mut files = Vec::new();
    collect_files(payload_dir, &mut files);
    files.sort();

    files
        .into_iter()
        .find(|path| has_extension(path, extensions))
}

/// Finds the first top-level payload file matching one of the provided extensions.
fn find_top_level_payload_file(payload_dir: &Path, extensions: &[&str]) -> Option<PathBuf> {
    let Ok(entries) = std::fs::read_dir(payload_dir) else {
        return None;
    };

    let mut files = entries
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| path.is_file())
        .collect::<Vec<_>>();
    files.sort();

    files
        .into_iter()
        .find(|path| has_extension(path, extensions))
}

/// Returns dependency MSIX packages under the payload `Dependencies` directory.
fn find_dependency_packages(payload_dir: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    collect_files(&payload_dir.join("Dependencies"), &mut files);
    files.retain(|path| has_extension(path, &["msix"]));
    files.sort();
    files
}

/// Returns whether a path extension matches any candidate case-insensitively.
fn has_extension(path: &Path, extensions: &[&str]) -> bool {
    path.extension()
        .and_then(|extension| extension.to_str())
        .map(|extension| {
            extensions
                .iter()
                .any(|candidate| extension.eq_ignore_ascii_case(candidate))
        })
        .unwrap_or(false)
}

/// Recursively collects files under a directory, ignoring unreadable directories.
fn collect_files(directory: &Path, files: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(directory) else {
        return;
    };

    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect_files(&path, files);
        } else {
            files.push(path);
        }
    }
}

/// Quotes a filesystem path as a single PowerShell string literal.
fn powershell_quote(path: &Path) -> String {
    powershell_quote_text(&path.to_string_lossy())
}

/// Quotes text as a single PowerShell string literal.
fn powershell_quote_text(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

/// Posts progress and status text to the Slint event loop.
fn set_progress(
    app_weak: &Weak<MainWindow>,
    progress: f32,
    title: &'static str,
    message: &'static str,
) {
    app_weak
        .upgrade_in_event_loop(move |handle| {
            handle.set_progress(progress);
            handle.set_state_title(SharedString::from(title));
            handle.set_state_message(SharedString::from(message));
        })
        .ok();
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(windows)]
    use std::io::Write;
    use std::path::Path;

    const TEST_SID: &str = "S-1-5-21-100-200-300-1001";

    #[cfg(windows)]
    fn assert_powershell_parses(script: &str) {
        let mut child = Command::new("powershell.exe")
            .args([
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
            ])
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        child
            .stdin
            .as_mut()
            .unwrap()
            .write_all(script.as_bytes())
            .unwrap();
        drop(child.stdin.take());
        let output = child.wait_with_output().unwrap();
        assert!(
            output.status.success(),
            "PowerShell syntax failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }

    #[test]
    fn final_installed_state_uses_actual_query_after_uninstall_failure() {
        assert!(final_installed_state(
            InstallerAction::Uninstall,
            &Err(String::from("remove failed")),
            true
        ));
    }

    #[test]
    fn final_installed_state_uses_actual_query_after_install_failure() {
        assert!(!final_installed_state(
            InstallerAction::Install,
            &Err(String::from("deploy failed")),
            false
        ));
    }

    #[test]
    fn native_architecture_gate_accepts_only_amd64() {
        assert_eq!(
            require_native_amd64(IMAGE_FILE_MACHINE_AMD64).unwrap(),
            "AMD64"
        );

        let arm64_error = require_native_amd64(IMAGE_FILE_MACHINE_ARM64).unwrap_err();
        assert!(arm64_error.contains("ARM64 is not supported"));
        assert!(require_native_amd64(0x014c).is_err());
        assert!(require_native_amd64(0).is_err());
    }

    #[test]
    fn native_architecture_query_uses_windows_api_not_environment() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let platform_code = source
            .split_once("fn inspect_supported_system(")
            .unwrap()
            .1
            .split_once("fn read_windows_build(")
            .unwrap()
            .0;
        let inherited_arch_variable = ["PROCESSOR", "_ARCHITECTURE"].concat();

        assert!(platform_code.contains("IsWow64Process2"));
        assert!(platform_code.contains("GetCurrentProcess"));
        assert!(platform_code.contains("require_native_amd64(query_native_machine()?)"));
        assert!(platform_code.contains("IMAGE_FILE_MACHINE_AMD64 => Ok(\"AMD64\")"));
        assert!(platform_code.contains("IMAGE_FILE_MACHINE_ARM64 => Err"));
        assert!(!platform_code.contains("std::env::var"));
        assert!(!platform_code.contains(&inherited_arch_variable));
    }

    #[test]
    fn repair_package_command_is_in_place_and_retryable() {
        let command =
            render_deploy_package_command(Path::new(r"D:\payload\ClashSharp.msix"), &[], true);

        assert!(command.starts_with("Add-AppxPackage"));
        assert!(command.contains("-Update"));
        assert!(command.contains("-ForceUpdateFromAnyVersion"));
        assert!(command.contains("-RetainFilesOnFailure"));
        assert!(!command.contains("Remove-AppxPackage"));
        assert!(!command.contains("-PreserveApplicationData"));
    }

    #[test]
    fn package_process_preflight_uses_exact_registration_path_and_token_owner() {
        let command = render_package_process_preflight_command(TEST_SID, false).unwrap();

        for required in [
            TEST_SID,
            PACKAGE_IDENTITY_NAME,
            PACKAGE_FAMILY_NAME,
            "if ($packages.Count -eq 0) { exit 0 }",
            "if ($packages.Count -ne 1)",
            "PackageFullName",
            "InstallLocation",
            "$installPrefix = $installRoot + [IO.Path]::DirectorySeparatorChar",
            "Win32_Process",
            "ExecutablePath",
            "GetOwnerSid",
            "[string]$owner.Sid -ceq $targetSid",
            "registered-package process token is ambiguous",
            "installer.app.running",
        ] {
            assert!(
                command.contains(required),
                "missing preflight contract: {required}"
            );
        }
        assert!(command.contains(&format!("exit {APP_RUNNING_EXIT_CODE}")));
        assert!(command.contains("Get-AppxPackage -Name $identityName"));
        assert!(!command.contains("Get-AppxPackage -User"));
        for forbidden in [
            "Stop-Process",
            "taskkill",
            "ProcessName",
            "-Name ClashSharp",
        ] {
            assert!(!command.contains(forbidden));
        }
    }

    #[test]
    fn elevated_package_process_preflight_queries_the_exact_target_user() {
        let command = render_package_process_preflight_command(TEST_SID, true).unwrap();

        assert!(command.contains("Get-AppxPackage -User $targetSid -Name $identityName"));
        assert!(command.contains("[string]$owner.Sid -ceq $targetSid"));
    }

    #[test]
    fn package_process_preflight_lock_is_acquired_before_operation_steps_and_uac() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let run_action = source.split_once("fn run_action(").unwrap().1;
        let run_action = run_action
            .split_once("fn final_installed_state(")
            .unwrap()
            .0;
        let acquire = source
            .split_once("fn acquire_installer_mutation_locks(")
            .unwrap();
        let acquire = acquire
            .1
            .split_once("fn open_exclusive_coordination_lock(")
            .unwrap()
            .0;
        let lock = run_action
            .find("acquire_installer_mutation_locks(")
            .unwrap();
        let steps = run_action
            .find("for step in operation_steps(operation)")
            .unwrap();

        assert!(lock < steps);
        assert_eq!(
            acquire
                .matches("ensure_current_user_package_stopped(target_sid)?")
                .count(),
            2
        );
        assert!(!run_action[..lock].contains("apply_machine_service("));
        assert!(!run_action[..lock].contains("uninstall_machine_resources_if_owner("));
    }

    #[test]
    fn package_mutation_locks_use_check_lock_check_for_the_operation_lifetime() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let acquire = source
            .split_once("fn acquire_installer_mutation_locks(")
            .unwrap()
            .1
            .split_once("fn open_exclusive_coordination_lock(")
            .unwrap()
            .0;
        let run_action = source
            .split_once("fn run_action(")
            .unwrap()
            .1
            .split_once("fn final_installed_state(")
            .unwrap()
            .0;

        assert_eq!(
            acquire
                .matches("ensure_current_user_package_stopped(target_sid)?")
                .count(),
            2
        );
        assert!(source.contains("options.share_mode(0)"));
        assert!(source.contains("InstallerMutation.lock"));
        assert!(source.contains("RecoveryWatchdog.lock"));
        assert!(run_action.contains("let mut package_mutation_locks"));
        assert!(run_action.contains("package_mutation_locks.acquire_recovery_lock(&target_sid)?"));
        assert!(run_action.contains("package_mutation_locks.release_recovery_lock()"));
        assert!(!run_action.contains("drop(package_mutation_locks)"));
    }

    #[test]
    fn direct_machine_helper_rechecks_supported_os_before_mutation() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let helper = source
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;
        let supported = helper.find("inspect_supported_system()?").unwrap();
        let elevation = helper.find("is_current_process_elevated()?").unwrap();
        let machine_folders = helper.find("query_machine_folders()?").unwrap();

        assert!(supported < elevation);
        assert!(supported < machine_folders);
    }

    #[test]
    fn direct_machine_helper_rechecks_target_app_after_uac_before_scripts() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let coordinator = source
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;

        assert_eq!(
            coordinator
                .matches("target_user_package_is_running(target_sid)?")
                .count(),
            3
        );
        let commit = coordinator
            .split_once("fn commit_installer_transaction(")
            .unwrap()
            .1;
        let first_recheck = commit
            .find("target_user_package_is_running(target_sid)?")
            .unwrap();
        let first_script = commit
            .find("let script_exit_code = run_powershell_stdin(&plan.render_apply_script()?)?;")
            .unwrap();
        let execute = coordinator
            .split_once("MachineHelperInvocation::Uninstall { target_sid } =>")
            .unwrap()
            .1
            .split_once("/// Creates or resumes the durable reservation")
            .unwrap()
            .0;
        let second_recheck = execute
            .find("target_user_package_is_running(target_sid)?")
            .unwrap();
        let second_script = execute
            .find("run_powershell_stdin(&resources.render_uninstall_script(target_sid)?)?")
            .unwrap();

        assert!(first_recheck < first_script);
        assert!(second_recheck < second_script);
    }

    #[test]
    fn machine_helper_holds_global_and_user_barriers_before_machine_facts() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let helper = source
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;

        let global_mutex = helper.find("acquire_machine_mutation_mutex()?").unwrap();
        let target_reader = helper
            .find("acquire_target_user_installer_mutation_reader(target_sid)?")
            .unwrap();
        let machine_folders = helper.find("query_machine_folders()?").unwrap();
        let previous_reader = helper
            .find(".map(acquire_target_user_installer_mutation_reader)")
            .unwrap();
        let service_query = helper.find("query_mihomo_service_exists()?").unwrap();

        assert!(global_mutex < target_reader);
        assert!(target_reader < machine_folders);
        assert!(previous_reader < service_query);
    }

    #[test]
    fn helper_reader_continues_parent_operation_admission_after_parent_exit() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let acquire = source
            .split_once("fn acquire_installer_mutation_locks(")
            .unwrap()
            .1
            .split_once("fn ensure_package_independent_lock_directory(")
            .unwrap()
            .0;
        let prepare = source
            .split_once("fn prepare_installer_mutation_reader(")
            .unwrap()
            .1
            .split_once("fn open_installer_mutation_reader(")
            .unwrap()
            .0;
        let reader = source
            .split_once("fn open_installer_mutation_reader(")
            .unwrap()
            .1
            .split_once("fn validate_coordination_lock_file(")
            .unwrap()
            .0;
        let helper = source
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;

        assert!(acquire.contains("prepare_installer_mutation_reader("));
        assert!(prepare.contains(".share_mode(0)"));
        assert!(reader.contains(".share_mode(FILE_SHARE_READ)"));
        assert!(helper.contains("_installer_mutation_reader"));
        assert!(helper.contains("acquire_target_user_installer_mutation_reader(target_sid)?"));
    }

    #[test]
    fn privileged_powershell_job_is_assigned_before_script_bytes_are_written() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let runner = source
            .split_once("fn run_powershell_stdin(")
            .unwrap()
            .1
            .split_once("fn successful_output_text(")
            .unwrap()
            .0;

        let create_job = runner.find("KillOnCloseJob::create()?").unwrap();
        let spawn = runner.find(".spawn()").unwrap();
        let assign = runner.find("job.assign(&child)").unwrap();
        let write = runner.find("stdin.write_all(script.as_bytes())").unwrap();
        assert!(create_job < spawn);
        assert!(spawn < assign);
        assert!(assign < write);
        assert!(source.contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"));
    }

    #[test]
    fn privileged_machine_scripts_are_streamed_and_exit_codes_are_propagated() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let helper = source
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;
        let runner = source
            .split_once("fn run_powershell_stdin(")
            .unwrap()
            .1
            .split_once("fn successful_output_text(")
            .unwrap()
            .0;

        for required in [
            "let script_exit_code = run_powershell_stdin(&plan.render_apply_script()?)?;",
            "run_powershell_stdin(&resources.render_uninstall_script(target_sid)?)?",
        ] {
            assert!(helper.contains(required), "missing stdin call: {required}");
        }
        assert_eq!(helper.matches("let script_exit_code").count(), 2);
        assert_eq!(helper.matches("Ok(script_exit_code)").count(), 2);
        let declaration = source
            .lines()
            .find(|line| line.starts_with("fn run_powershell_stdin("))
            .unwrap();
        assert_eq!(
            declaration,
            "fn run_powershell_stdin(script: &str) -> Result<i32, String> {"
        );
        for required in [
            ".stdin(Stdio::piped())",
            "[Console]::In.ReadToEnd()",
            ".write_all(script.as_bytes())",
            "return Ok(APP_RUNNING_EXIT_CODE)",
            "Ok(0)",
        ] {
            assert!(
                runner.contains(required),
                "missing stdin contract: {required}"
            );
        }
        let forbidden_apply = ["run_powershell(", "&plan.render_apply_script()"].concat();
        let forbidden_uninstall = [
            "run_powershell(",
            "&resources.render_uninstall_script(target_sid)",
        ]
        .concat();
        assert!(!helper.contains(&forbidden_apply));
        assert!(!helper.contains(&forbidden_uninstall));
    }

    #[test]
    fn durable_transaction_wiring_prepares_before_package_and_clears_only_after_verified() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let run_action = source
            .split_once("fn run_action(")
            .unwrap()
            .1
            .split_once("fn final_installed_state(")
            .unwrap()
            .0;
        let prepare = run_action.find("prepare_machine_transaction(").unwrap();
        let deploy = run_action.find("deploy_package(").unwrap();
        let commit = run_action.find("commit_machine_transaction(").unwrap();
        assert!(prepare < deploy && deploy < commit);
        assert!(run_action.contains("installer.transaction.package_state_uncertain"));
        assert!(!run_action.contains("abort_machine_transaction"));

        let helper_commit = source
            .split_once("fn commit_installer_transaction(")
            .unwrap()
            .1
            .split_once("fn fixed_sibling_payload_directory(")
            .unwrap()
            .0;
        let registration = helper_commit
            .find("query_target_package_registration_if_present(target_sid)?")
            .unwrap();
        let payload_verify = helper_commit
            .find("verify_registered_machine_payload(registration.install_location())")
            .unwrap();
        let package_phase = helper_commit
            .find("InstallerTransactionPhase::PackageCommitted")
            .unwrap();
        let machine_apply = helper_commit
            .find("run_powershell_stdin(&plan.render_apply_script()?)?")
            .unwrap();
        let machine_phase = helper_commit
            .find("InstallerTransactionPhase::MachineCommitted")
            .unwrap();
        let verified_phase = helper_commit
            .rfind("InstallerTransactionPhase::Verified")
            .unwrap();
        let release_marker = helper_commit
            .find("clear_installer_transaction(common_application_data, transaction_path)?")
            .unwrap();
        assert!(registration < payload_verify);
        assert!(payload_verify < package_phase && package_phase < machine_apply);
        assert!(machine_apply < machine_phase && machine_phase < verified_phase);
        assert!(verified_phase < release_marker);
    }

    #[test]
    fn transaction_journal_script_is_fixed_atomic_write_through_and_user_read_only() {
        let transaction_id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        let script = render_installer_transaction_write_script(
            Path::new(r"C:\ProgramData"),
            Path::new(r"C:\ProgramData\ClashSharp\Installer\transaction.json"),
            transaction_id,
            r#"{"schema":1}"#,
        );

        for required in [
            r"C:\ProgramData\ClashSharp\Installer\transaction.json",
            "S-1-5-18",
            "S-1-5-32-544",
            "S-1-5-32-545",
            "FileOptions]::WriteThrough",
            "$stream.Flush($true)",
            "[IO.File]::Replace($tempPath, $journalPath, $null, $true)",
            "FileSystemRights]::Read",
        ] {
            assert!(
                script.contains(required),
                "missing journal contract: {required}"
            );
        }
        assert!(!script.contains("authenticationToken"));
        assert!(!script.contains("Invoke-WebRequest"));
    }

    #[cfg(windows)]
    #[test]
    fn embedded_coordinator_powershell_scripts_have_valid_syntax() {
        let transaction_id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        let program_data = Path::new(r"C:\ProgramData");
        let journal = Path::new(r"C:\ProgramData\ClashSharp\Installer\transaction.json");
        for script in [
            render_installer_transaction_write_script(
                program_data,
                journal,
                transaction_id,
                r#"{"schema":1}"#,
            ),
            render_installer_transaction_clear_script(program_data, journal),
            render_installer_transaction_protection_script(journal),
            render_package_process_preflight_command(TEST_SID, true).unwrap(),
            render_parent_mutating_powershell_command(
                "Get-Date | Out-Null",
                Path::new(r"C:\Users\owner\AppData\Local\ClashSharp\InstallerMutation.lock"),
            ),
        ] {
            assert_powershell_parses(&script);
        }
    }

    #[test]
    fn abandoned_machine_mutex_keeps_transferred_ownership_for_reconciliation() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let mutex = source
            .split_once("fn acquire_machine_mutation_mutex(")
            .unwrap()
            .1
            .split_once("fn execute_machine_helper(")
            .unwrap()
            .0;

        assert!(mutex.contains("WAIT_ABANDONED_0 => Ok(MachineMutationMutexGuard { handle })"));
        assert!(!mutex.contains("lock_abandoned_repair_required"));
    }

    #[test]
    fn current_user_package_mutation_is_also_kill_on_parent_close() {
        let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let parent = source
            .split_once("fn run_parent_mutating_powershell(")
            .unwrap()
            .1
            .split_once("fn render_parent_mutating_powershell_command(")
            .unwrap()
            .0;
        assert!(parent.contains("run_powershell_stdin("));
        assert!(source.contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"));
    }

    #[test]
    fn parses_version_from_payload_package_name() {
        let path = Path::new("ClashSharp_1.2.3.4_x64.msix");

        assert_eq!(
            parse_version_from_package_name(path),
            Some(String::from("1.2.3.4"))
        );
    }

    #[test]
    fn rejects_payload_name_without_version_segment() {
        let path = Path::new("ClashSharp_x64.msix");

        assert_eq!(parse_version_from_package_name(path), None);
    }

    #[test]
    fn compact_path_keeps_short_paths_unchanged() {
        let path = Path::new(r"D:\ClashSharp\Installer\payload");

        assert_eq!(compact_path(path), r"D:\ClashSharp\Installer\payload");
    }

    #[test]
    fn compact_path_keeps_tail_for_long_paths() {
        let path = Path::new(r"D:\Coding\ClashSharp\ClashSharp\Installer\target\debug\payload");
        let compact = compact_path(path);

        assert!(compact.starts_with("..."));
        assert!(compact.ends_with(r"Installer\target\debug\payload"));
        assert!(compact.chars().count() <= 44);
    }

    #[test]
    fn reads_identity_version_from_manifest_text() {
        let manifest = r#"
<Package>
  <Identity
    Name="67dc1dc3-13fd-46c5-84f4-2932d94b566f"
    Publisher="CN=linzh"
    Version="2.3.4.5" />
</Package>
"#;

        assert_eq!(
            read_manifest_version_text(manifest),
            Some(String::from("2.3.4.5"))
        );
    }

    #[test]
    fn parses_hex_registry_dword_output() {
        let output = r#"
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
    AppsUseLightTheme    REG_DWORD    0x0
"#;

        assert_eq!(parse_registry_dword_output(output), Some(0));
    }

    #[test]
    fn parses_decimal_registry_dword_output() {
        let output = "AppsUseLightTheme    REG_DWORD    1";

        assert_eq!(parse_registry_dword_output(output), Some(1));
    }
}
