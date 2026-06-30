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
    let options = CliOptions::parse(&args[1..])?;
    let repo_root = options.repo_root()?;
    let layout = default_layout(repo_root);

    match command {
        "plan" => {
            print!("{}", render_dry_run_plan(&layout));
            Ok(())
        }
        "emit-wsb" => {
            let shared_dir = options
                .shared_dir
                .clone()
                .unwrap_or_else(|| layout.shared_dir.clone());
            let wsb_file = options
                .wsb_file
                .clone()
                .unwrap_or_else(|| layout.wsb_file.clone());

            fs::create_dir_all(&shared_dir).map_err(|error| {
                format!(
                    "failed to create shared directory {}: {error}",
                    shared_dir.display()
                )
            })?;
            if let Some(parent) = wsb_file.parent() {
                fs::create_dir_all(parent).map_err(|error| {
                    format!(
                        "failed to create WSB directory {}: {error}",
                        parent.display()
                    )
                })?;
            }

            let config = SandboxConfig {
                shared_dir,
                logon_command: SANDBOX_LOGON_COMMAND.to_string(),
            };

            fs::write(&wsb_file, config.render_wsb())
                .map_err(|error| format!("failed to write {}: {error}", wsb_file.display()))?;

            println!("{}", wsb_file.display());
            Ok(())
        }
        "--help" | "-h" | "help" => {
            print_help();
            Ok(())
        }
        other => Err(format!("unknown command '{other}'")),
    }
}

#[derive(Debug, Default)]
struct CliOptions {
    repo_root: Option<PathBuf>,
    shared_dir: Option<PathBuf>,
    wsb_file: Option<PathBuf>,
}

impl CliOptions {
    fn parse(args: &[String]) -> Result<Self, String> {
        let mut options = Self::default();
        let mut index = 0;

        while index < args.len() {
            match args[index].as_str() {
                "--repo-root" => {
                    let value = args
                        .get(index + 1)
                        .ok_or_else(|| String::from("--repo-root requires a path"))?;
                    options.repo_root = Some(PathBuf::from(value));
                    index += 2;
                }
                "--shared-dir" => {
                    let value = args
                        .get(index + 1)
                        .ok_or_else(|| String::from("--shared-dir requires a path"))?;
                    options.shared_dir = Some(PathBuf::from(value));
                    index += 2;
                }
                "--wsb-file" => {
                    let value = args
                        .get(index + 1)
                        .ok_or_else(|| String::from("--wsb-file requires a path"))?;
                    options.wsb_file = Some(PathBuf::from(value));
                    index += 2;
                }
                unknown => return Err(format!("unknown argument '{unknown}'")),
            }
        }

        Ok(options)
    }

    fn repo_root(&self) -> Result<PathBuf, String> {
        repo_root_from_option(self.repo_root.clone())
    }
}

fn repo_root_from_option(repo_root: Option<PathBuf>) -> Result<PathBuf, String> {
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
        "  emit-wsb  Create the shared directory and write a Windows Sandbox .wsb file.\n",
        "\n",
        "Options:\n",
        "  --repo-root <path>   Repository root. Auto-detected when omitted.\n",
        "  --shared-dir <path>  Mapped host folder for emit-wsb.\n",
        "  --wsb-file <path>    Output .wsb path for emit-wsb.\n"
    ));
}
