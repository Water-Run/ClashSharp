use std::env;
use std::fs;
use std::path::PathBuf;

use clashsharp_sandbox_test::paths::{default_layout, find_repo_root};
use clashsharp_sandbox_test::plan::render_dry_run_plan;
use clashsharp_sandbox_test::sandbox::SandboxConfig;

const SANDBOX_LOGON_COMMAND: &str = r"powershell.exe -ExecutionPolicy Bypass -File C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox\scripts\Run-InSandbox.ps1";

fn main() {
    if let Err(error) = run(env::args().skip(1).collect()) {
        eprintln!("error: {error}");
        std::process::exit(1);
    }
}

fn run(args: Vec<String>) -> Result<(), String> {
    let command = args.first().map(String::as_str).unwrap_or("plan");
    let repo_root = repo_root_from_args(&args[1..])?;
    let layout = default_layout(repo_root);

    match command {
        "plan" => {
            print!("{}", render_dry_run_plan(&layout));
            Ok(())
        }
        "emit-wsb" => {
            fs::create_dir_all(&layout.shared_dir).map_err(|error| {
                format!(
                    "failed to create shared directory {}: {error}",
                    layout.shared_dir.display()
                )
            })?;

            let config = SandboxConfig {
                shared_dir: layout.shared_dir,
                logon_command: SANDBOX_LOGON_COMMAND.to_string(),
            };

            fs::write(&layout.wsb_file, config.render_wsb()).map_err(|error| {
                format!("failed to write {}: {error}", layout.wsb_file.display())
            })?;

            println!("{}", layout.wsb_file.display());
            Ok(())
        }
        "--help" | "-h" | "help" => {
            print_help();
            Ok(())
        }
        other => Err(format!("unknown command '{other}'")),
    }
}

fn repo_root_from_args(args: &[String]) -> Result<PathBuf, String> {
    let mut repo_root = None;
    let mut index = 0;

    while index < args.len() {
        match args[index].as_str() {
            "--repo-root" => {
                let value = args
                    .get(index + 1)
                    .ok_or_else(|| String::from("--repo-root requires a path"))?;
                repo_root = Some(PathBuf::from(value));
                index += 2;
            }
            unknown => return Err(format!("unknown argument '{unknown}'")),
        }
    }

    if let Some(path) = repo_root {
        return Ok(path);
    }

    let current_dir =
        env::current_dir().map_err(|error| format!("failed to read current directory: {error}"))?;
    find_repo_root(&current_dir)
        .ok_or_else(|| format!("could not find repo root from {}", current_dir.display()))
}

fn print_help() {
    println!(concat!(
        "Usage: clashsharp-sandbox-test [plan|emit-wsb] [--repo-root <path>]\n",
        "\n",
        "Commands:\n",
        "  plan      Print the dry-run host and Sandbox execution plan.\n",
        "  emit-wsb  Create .sandbox/shared and write ClashSharpSandbox.wsb.\n"
    ));
}
