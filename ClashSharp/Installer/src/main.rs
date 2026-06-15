use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;

use slint::{ComponentHandle, SharedString, Weak};

slint::include_modules!();

const PACKAGE_NAME: &str = "67dc1dc3-13fd-46c5-84f4-2932d94b566f";

#[derive(Clone, Copy)]
enum InstallerAction {
    Install,
    Repair,
    Uninstall,
}

struct InstallerContext {
    payload_dir: PathBuf,
    package_path: Option<PathBuf>,
    certificate_path: Option<PathBuf>,
    dependency_paths: Vec<PathBuf>,
    is_installed: bool,
}

fn main() -> Result<(), slint::PlatformError> {
    let app = MainWindow::new()?;
    let app_weak = app.as_weak();

    refresh_state(&app);

    app.on_exit_app(|| {
        std::process::exit(0);
    });

    app.on_refresh_state({
        let app_weak = app_weak.clone();
        move || {
            if let Some(handle) = app_weak.upgrade() {
                refresh_state(&handle);
            }
        }
    });

    app.on_install({
        let app_weak = app_weak.clone();
        move || run_action_async(app_weak.clone(), InstallerAction::Install)
    });

    app.on_repair({
        let app_weak = app_weak.clone();
        move || run_action_async(app_weak.clone(), InstallerAction::Repair)
    });

    app.on_uninstall({
        let app_weak = app_weak.clone();
        move || run_action_async(app_weak.clone(), InstallerAction::Uninstall)
    });

    app.run()
}

fn run_action_async(app_weak: Weak<MainWindow>, action: InstallerAction) {
    let Some(handle) = app_weak.upgrade() else {
        return;
    };

    handle.set_busy(true);
    handle.set_progress(0.05);
    handle.set_status_text(SharedString::from(match action {
        InstallerAction::Install => "Preparing installation...",
        InstallerAction::Repair => "Preparing repair...",
        InstallerAction::Uninstall => "Preparing uninstall...",
    }));

    thread::spawn(move || {
        let result = run_action(&app_weak, action);
        app_weak
            .upgrade_in_event_loop(move |handle| {
                handle.set_busy(false);
                match result {
                    Ok(message) => {
                        handle.set_progress(1.0);
                        handle.set_status_text(SharedString::from(message));
                    }
                    Err(error) => {
                        handle.set_progress(0.0);
                        handle.set_status_text(SharedString::from(format!("Error: {error}")));
                    }
                }
                refresh_state(&handle);
            })
            .ok();
    });
}

fn run_action(app_weak: &Weak<MainWindow>, action: InstallerAction) -> Result<String, String> {
    let context = build_context()?;
    ensure_windows_11_x64()?;

    match action {
        InstallerAction::Install => {
            let package_path = context
                .package_path
                .as_ref()
                .ok_or_else(|| String::from("No .msix or .msixbundle package found in payload."))?;

            set_progress(app_weak, 0.25, "Installing package certificate...");
            if let Some(certificate_path) = context.certificate_path.as_ref() {
                install_certificate(certificate_path)?;
            }

            set_progress(app_weak, 0.65, "Deploying MSIX package...");
            deploy_package(package_path, &context.dependency_paths)?;

            Ok(String::from("Clash# has been deployed."))
        }
        InstallerAction::Repair => {
            let package_path = context
                .package_path
                .as_ref()
                .ok_or_else(|| String::from("No .msix or .msixbundle package found in payload."))?;

            set_progress(app_weak, 0.20, "Installing package certificate...");
            if let Some(certificate_path) = context.certificate_path.as_ref() {
                install_certificate(certificate_path)?;
            }

            if context.is_installed {
                set_progress(app_weak, 0.45, "Removing existing package...");
                uninstall_package()?;
            }

            set_progress(app_weak, 0.75, "Redeploying MSIX package...");
            deploy_package(package_path, &context.dependency_paths)?;

            Ok(String::from("Clash# has been redeployed."))
        }
        InstallerAction::Uninstall => {
            if !context.is_installed {
                return Ok(String::from("Clash# is not installed."));
            }

            set_progress(app_weak, 0.55, "Removing installed package...");
            uninstall_package()?;
            Ok(String::from("Clash# has been uninstalled."))
        }
    }
}

fn refresh_state(handle: &MainWindow) {
    let context = build_context();
    let is_supported = ensure_windows_11_x64().is_ok();
    let is_installed = is_package_installed();
    let status_text = match context {
        Ok(context) => {
            let package = context
                .package_path
                .as_ref()
                .and_then(|path| path.file_name())
                .and_then(|name| name.to_str())
                .unwrap_or("not found");
            let certificate = context
                .certificate_path
                .as_ref()
                .and_then(|path| path.file_name())
                .and_then(|name| name.to_str())
                .unwrap_or("not found");

            format!(
                "System: {} | Payload: {} | Package: {} | Certificate: {}",
                if is_supported {
                    "Windows 11 x64"
                } else {
                    "unsupported"
                },
                context.payload_dir.to_string_lossy(),
                package,
                certificate
            )
        }
        Err(error) => format!("Payload scan failed: {error}"),
    };

    handle.set_installed(is_installed);
    handle.set_supported(is_supported);
    handle.set_progress(0.0);
    handle.set_status_text(SharedString::from(status_text));
    handle.set_mode_text(SharedString::from(if is_installed {
        "Maintenance mode"
    } else {
        "Install mode"
    }));
}

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

fn ensure_windows_11_x64() -> Result<(), String> {
    if !cfg!(target_pointer_width = "64") {
        return Err(String::from("Installer must run as a 64-bit process."));
    }

    let architecture = std::env::var("PROCESSOR_ARCHITECTURE").unwrap_or_default();
    if !architecture.contains("64") {
        return Err(String::from("Windows 11 x64 is required."));
    }

    let output = Command::new("reg")
        .args([
            "query",
            r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "/v",
            "CurrentBuildNumber",
        ])
        .output()
        .map_err(|error| format!("Windows version query failed: {error}"))?;
    let text = String::from_utf8_lossy(&output.stdout);
    let build = text
        .split_whitespace()
        .filter_map(|part| part.parse::<u32>().ok())
        .next_back()
        .ok_or_else(|| String::from("Windows build number could not be read."))?;

    if build < 22000 {
        return Err(String::from("Windows 11 build 22000 or later is required."));
    }

    Ok(())
}

fn install_certificate(certificate_path: &Path) -> Result<(), String> {
    run_powershell(&format!(
        "Import-Certificate -FilePath {} -CertStoreLocation Cert:\\CurrentUser\\TrustedPeople | Out-Null",
        powershell_quote(certificate_path)
    ))
}

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

fn uninstall_package() -> Result<(), String> {
    run_powershell(&format!(
        "$pkg = Get-AppxPackage -Name {}; if ($null -ne $pkg) {{ Remove-AppxPackage -Package $pkg.PackageFullName }}",
        powershell_quote_text(PACKAGE_NAME)
    ))
}

fn is_package_installed() -> bool {
    run_powershell_capture(&format!(
        "if (Get-AppxPackage -Name {}) {{ exit 0 }} else {{ exit 1 }}",
        powershell_quote_text(PACKAGE_NAME)
    ))
    .map(|output| output.status.success())
    .unwrap_or(false)
}

fn run_powershell(command: &str) -> Result<(), String> {
    let output = run_powershell_capture(command)?;
    if output.status.success() {
        return Ok(());
    }

    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    Err(if stderr.is_empty() { stdout } else { stderr })
}

fn run_powershell_capture(command: &str) -> Result<std::process::Output, String> {
    Command::new("powershell.exe")
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command])
        .output()
        .map_err(|error| format!("PowerShell failed to start: {error}"))
}

fn find_payload_file(payload_dir: &Path, extensions: &[&str]) -> Option<PathBuf> {
    let mut files = Vec::new();
    collect_files(payload_dir, &mut files);
    files.sort();

    files.into_iter().find(|path| {
        path.extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| {
                extensions
                    .iter()
                    .any(|candidate| extension.eq_ignore_ascii_case(candidate))
            })
            .unwrap_or(false)
    })
}

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

    files.into_iter().find(|path| {
        path.extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| {
                extensions
                    .iter()
                    .any(|candidate| extension.eq_ignore_ascii_case(candidate))
            })
            .unwrap_or(false)
    })
}

fn find_dependency_packages(payload_dir: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    collect_files(&payload_dir.join("Dependencies"), &mut files);
    files.retain(|path| {
        path.extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| extension.eq_ignore_ascii_case("msix"))
            .unwrap_or(false)
    });
    files.sort();
    files
}

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

fn powershell_quote(path: &Path) -> String {
    powershell_quote_text(&path.to_string_lossy())
}

fn powershell_quote_text(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn set_progress(app_weak: &Weak<MainWindow>, progress: f32, status: &'static str) {
    app_weak
        .upgrade_in_event_loop(move |handle| {
            handle.set_progress(progress);
            handle.set_status_text(SharedString::from(status));
        })
        .ok();
}
