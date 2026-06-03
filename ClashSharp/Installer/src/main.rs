use std::path::{Path, PathBuf};
use std::thread;
use std::time::Duration;

use slint::{ComponentHandle, SharedString, Weak};

slint::include_modules!();

fn default_install_dir() -> PathBuf {
    let program_files =
        std::env::var("ProgramFiles").unwrap_or_else(|_| String::from("C:\\Program Files"));
    PathBuf::from(program_files).join("ClashSharp")
}

fn collect_payload_entries(payload_dir: &Path) -> Vec<PathBuf> {
    let mut entries: Vec<PathBuf> = payload_dir
        .read_dir()
        .map(|dir| {
            dir.filter_map(|e| e.ok())
                .map(|e| e.path())
                .filter(|p| p.file_name().map_or(false, |n| n != ".gitkeep"))
                .collect()
        })
        .unwrap_or_default();
    entries.sort();
    entries
}

fn copy_dir_recursive(src: &Path, dst: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dst)?;
    for entry in std::fs::read_dir(src)? {
        let entry = entry?;
        let target = dst.join(entry.file_name());
        if entry.file_type()?.is_dir() {
            copy_dir_recursive(&entry.path(), &target)?;
        } else {
            std::fs::copy(entry.path(), &target)?;
        }
    }
    Ok(())
}

fn run_install(
    app_weak: &Weak<MainWindow>,
    install_dir: &Path,
    payload_dir: &Path,
) -> Result<(), String> {
    let entries = collect_payload_entries(payload_dir);

    if entries.is_empty() {
        return Err(String::from(
            "payload directory is empty, place files to install first",
        ));
    }

    std::fs::create_dir_all(install_dir).map_err(|e| format!("mkdir failed: {e}"))?;

    let total = entries.len();

    for (index, entry) in entries.iter().enumerate() {
        let file_name = entry
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("unknown")
            .to_owned();

        let progress = (index as f64) / (total as f64);
        let text = format!("Copying {file_name}");

        app_weak
            .upgrade_in_event_loop(move |h| {
                h.set_progress(progress as f32);
                h.set_status_text(SharedString::from(text));
            })
            .ok();

        let target = install_dir.join(entry.file_name().unwrap());

        if entry.is_dir() {
            copy_dir_recursive(entry, &target).map_err(|e| format!("copy dir failed: {e}"))?;
        } else {
            std::fs::copy(entry, &target).map_err(|e| format!("copy file failed: {e}"))?;
        }

        thread::sleep(Duration::from_millis(50));
    }

    Ok(())
}

fn main() -> Result<(), slint::PlatformError> {
    let app = MainWindow::new()?;

    let default_dir = default_install_dir();
    app.set_install_dir(SharedString::from(
        default_dir.to_string_lossy().to_string(),
    ));

    let app_weak = app.as_weak();

    app.on_exit_app(|| {
        std::process::exit(0);
    });

    app.on_browse({
        let app_weak = app_weak.clone();
        move || {
            if let Some(dir) = rfd::FileDialog::new().pick_folder() {
                app_weak
                    .upgrade_in_event_loop(move |h| {
                        h.set_install_dir(SharedString::from(
                            dir.to_string_lossy().to_string(),
                        ));
                    })
                    .ok();
            }
        }
    });

    app.on_install(move || {
        let handle = match app_weak.upgrade() {
            Some(h) => h,
            None => return,
        };

        let install_dir = PathBuf::from(handle.get_install_dir().to_string());

        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."));
        let payload_dir = exe_dir.join("payload");

        handle.set_installing(true);

        let app_weak2 = app_weak.clone();
        let install_dir_clone = install_dir.clone();

        thread::spawn(move || {
            let result = run_install(&app_weak2, &install_dir_clone, &payload_dir);

            app_weak2
                .upgrade_in_event_loop(move |h| {
                    h.set_installing(false);
                    match result {
                        Ok(()) => {
                            h.set_progress(1.0);
                            h.set_status_text(SharedString::from(
                                "Installation complete — you may close this window",
                            ));
                        }
                        Err(e) => {
                            h.set_status_text(SharedString::from(format!("Error: {e}")));
                        }
                    }
                })
                .ok();
        });
    });

    app.run()
}
