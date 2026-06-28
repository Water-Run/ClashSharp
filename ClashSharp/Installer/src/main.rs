#![cfg_attr(windows, windows_subsystem = "windows")]

use std::cell::RefCell;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;

use clashsharp_installer::metadata::{
    compact_path, parse_version_from_package_name, read_manifest_version_text,
};
use slint::{ComponentHandle, SharedString, Weak};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

slint::include_modules!();

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

const PACKAGE_NAME: &str = "67dc1dc3-13fd-46c5-84f4-2932d94b566f";
const GITHUB_URL: &str = "https://github.com/Water-Run/ClashSharp";
const CLASHSHARP_LICENSE: &str = "AGPL-3.0";
static ACTION_RUNNING: AtomicBool = AtomicBool::new(false);

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

    match action {
        InstallerAction::Install => {
            let package_path = required_package_path(&context)?;
            let certificate_path = required_certificate_path(&context)?;

            set_progress(
                app_weak,
                0.22,
                text.certificate_title,
                text.certificate_message,
            );
            install_certificate(certificate_path)?;

            set_progress(app_weak, 0.68, text.package_title, text.package_message);
            deploy_package(package_path, &context.dependency_paths)?;

            Ok(text.installed_done)
        }
        InstallerAction::Repair => {
            let package_path = required_package_path(&context)?;
            let certificate_path = required_certificate_path(&context)?;

            set_progress(
                app_weak,
                0.18,
                text.certificate_title,
                text.certificate_message,
            );
            install_certificate(certificate_path)?;

            if context.is_installed {
                set_progress(app_weak, 0.42, text.removing_title, text.removing_message);
                uninstall_package()?;
            }

            set_progress(app_weak, 0.72, text.package_title, text.package_message);
            deploy_package(package_path, &context.dependency_paths)?;

            Ok(text.repaired_done)
        }
        InstallerAction::Uninstall => {
            set_progress(app_weak, 0.36, text.removing_title, text.removing_message);
            cleanup_installed_services()?;

            if !context.is_installed {
                return Ok(text.uninstalled_done);
            }

            set_progress(app_weak, 0.58, text.uninstall_title, text.uninstall_message);
            uninstall_package()?;
            Ok(text.uninstalled_done)
        }
    }
}

/// Returns the UI installation state after an action using a fresh package query result.
fn final_installed_state(
    _action: InstallerAction,
    _result: &Result<&'static str, String>,
    currently_installed: bool,
) -> bool {
    currently_installed
}

/// Returns the required MSIX or MSIX bundle path from the payload.
fn required_package_path(context: &InstallerContext) -> Result<&Path, String> {
    context
        .package_path
        .as_deref()
        .ok_or_else(|| String::from("No .msix or .msixbundle package found in payload."))
}

/// Returns the required certificate path from the payload.
fn required_certificate_path(context: &InstallerContext) -> Result<&Path, String> {
    context
        .certificate_path
        .as_deref()
        .ok_or_else(|| String::from("No certificate file found in payload."))
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

    let architecture = std::env::var("PROCESSOR_ARCHITECTURE").unwrap_or_default();
    if !architecture.contains("64") {
        return Err(String::from("Windows 11 x64 is required."));
    }

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

/// Reads the Windows build number from the registry.
fn read_windows_build() -> Result<u32, String> {
    let output = hidden_command("reg")
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
fn install_certificate(certificate_path: &Path) -> Result<(), String> {
    run_powershell(&format!(
        "Import-Certificate -FilePath {} -CertStoreLocation Cert:\\CurrentUser\\TrustedPeople | Out-Null",
        powershell_quote(certificate_path)
    ))
}

/// Deploys the MSIX package with any runtime dependency packages found in payload.
fn deploy_package(package_path: &Path, dependency_paths: &[PathBuf]) -> Result<(), String> {
    if dependency_paths.is_empty() {
        return run_powershell(&format!(
            "Add-AppxPackage -Path {} -ForceApplicationShutdown",
            powershell_quote(package_path)
        ));
    }

    let dependencies = dependency_paths
        .iter()
        .map(|path| powershell_quote(path))
        .collect::<Vec<_>>()
        .join(",");

    run_powershell(&format!(
        "Add-AppxPackage -Path {} -DependencyPath @({}) -ForceApplicationShutdown",
        powershell_quote(package_path),
        dependencies
    ))
}

/// Removes the installed Clash# MSIX package for the current user.
fn uninstall_package() -> Result<(), String> {
    run_powershell(&format!(
        "$pkg = Get-AppxPackage -Name {}; if ($null -ne $pkg) {{ Remove-AppxPackage -Package $pkg.PackageFullName }}",
        powershell_quote_text(PACKAGE_NAME)
    ))
}

/// Removes services and startup helpers that can survive package removal.
fn cleanup_installed_services() -> Result<(), String> {
    uninstall_mihomo_service()?;
    uninstall_startup_restore_fallback()
}

/// Stops and deletes the optional transparent-proxy Windows service.
fn uninstall_mihomo_service() -> Result<(), String> {
    run_powershell(
        "$name = 'ClashSharpMihomo'; \
         $svc = Get-Service -Name $name -ErrorAction SilentlyContinue; \
         if ($null -ne $svc) { \
             Stop-Service -Name $name -Force -ErrorAction SilentlyContinue; \
             & sc.exe delete $name | Out-Null; \
             if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) { exit $LASTEXITCODE } \
         }",
    )
}

/// Deletes the current-user startup restore fallback registration.
fn uninstall_startup_restore_fallback() -> Result<(), String> {
    run_powershell(
        "Remove-ItemProperty \
            -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' \
            -Name 'ClashSharp.ProxyRestoreFallback' \
            -ErrorAction SilentlyContinue",
    )
}

/// Returns whether the Clash# package is installed for the current user.
fn is_package_installed() -> bool {
    run_powershell_capture(&format!(
        "if (Get-AppxPackage -Name {}) {{ exit 0 }} else {{ exit 1 }}",
        powershell_quote_text(PACKAGE_NAME)
    ))
    .map(|output| output.status.success())
    .unwrap_or(false)
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
            admin_hint: "安装证书时可能需要管理员确认",
            preparing_install: "正在准备安装",
            preparing_repair: "正在准备修补",
            preparing_uninstall: "正在准备卸载",
            certificate_title: "正在安装证书",
            certificate_message: "正在导入 Clash# 包证书。",
            removing_title: "正在移除当前安装",
            removing_message: "正在卸载现有 MSIX 包以便重新部署。",
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
            admin_hint: "安裝憑證時可能需要系統管理員確認",
            preparing_install: "正在準備安裝",
            preparing_repair: "正在準備修補",
            preparing_uninstall: "正在準備解除安裝",
            certificate_title: "正在安裝憑證",
            certificate_message: "正在匯入 Clash# 包憑證。",
            removing_title: "正在移除目前安裝",
            removing_message: "正在解除安裝既有 MSIX 包以便重新部署。",
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
            admin_hint: "Certificate installation may require administrator confirmation",
            preparing_install: "Preparing installation",
            preparing_repair: "Preparing repair",
            preparing_uninstall: "Preparing uninstall",
            certificate_title: "Installing certificate",
            certificate_message: "Importing the Clash# package certificate.",
            removing_title: "Removing current installation",
            removing_message: "Removing the existing MSIX package before redeployment.",
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
            admin_hint: "Установка сертификата может потребовать подтверждения администратора",
            preparing_install: "Подготовка установки",
            preparing_repair: "Подготовка исправления",
            preparing_uninstall: "Подготовка удаления",
            certificate_title: "Установка сертификата",
            certificate_message: "Импортируется сертификат пакета Clash#.",
            removing_title: "Удаление текущей установки",
            removing_message: "Удаляется существующий пакет MSIX перед повторным развертыванием.",
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
            admin_hint: "L'installation du certificat peut demander une confirmation administrateur",
            preparing_install: "Preparation de l'installation",
            preparing_repair: "Preparation de la reparation",
            preparing_uninstall: "Preparation de la desinstallation",
            certificate_title: "Installation du certificat",
            certificate_message: "Importation du certificat du paquet Clash#.",
            removing_title: "Suppression de l'installation actuelle",
            removing_message: "Suppression du paquet MSIX existant avant le redeploiement.",
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
            admin_hint: "Die Zertifikatinstallation kann eine Administratorbestaetigung erfordern",
            preparing_install: "Installation wird vorbereitet",
            preparing_repair: "Reparatur wird vorbereitet",
            preparing_uninstall: "Deinstallation wird vorbereitet",
            certificate_title: "Zertifikat wird installiert",
            certificate_message: "Das Clash#-Paketzertifikat wird importiert.",
            removing_title: "Aktuelle Installation wird entfernt",
            removing_message: "Das vorhandene MSIX-Paket wird vor der erneuten Bereitstellung entfernt.",
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

/// Runs a PowerShell command and returns stderr/stdout text when it fails.
fn run_powershell(command: &str) -> Result<(), String> {
    let output = run_powershell_capture(command)?;
    if output.status.success() {
        return Ok(());
    }

    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    Err(if stderr.is_empty() { stdout } else { stderr })
}

/// Runs a PowerShell command and returns the raw process output.
fn run_powershell_capture(command: &str) -> Result<std::process::Output, String> {
    hidden_command("powershell.exe")
        .args([
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            command,
        ])
        .output()
        .map_err(|error| format!("PowerShell failed to start: {error}"))
}

/// Creates a command configured to avoid flashing a console window on Windows.
fn hidden_command(program: &str) -> Command {
    let mut command = Command::new(program);
    #[cfg(windows)]
    {
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command
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
    use std::path::Path;

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
