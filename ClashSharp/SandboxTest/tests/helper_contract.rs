use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use clashsharp_sandbox_test::paths::{default_layout, find_repo_root};
use clashsharp_sandbox_test::plan::render_dry_run_plan;
use clashsharp_sandbox_test::report::validate_report;
use clashsharp_sandbox_test::sandbox::SandboxConfig;
use clashsharp_sandbox_test::scenario::{Scenario, parse_scenario_selection, render_scenario_plan};

#[test]
fn finds_repo_root_by_solution_marker_from_nested_directory() {
    let fixture = TempFixture::new("repo-root");
    let repo = fixture.path();
    fs::create_dir_all(repo.join("ClashSharp")).unwrap();
    fs::write(repo.join("README.md"), "fixture").unwrap();
    fs::write(
        repo.join("ClashSharp").join("ClashSharp.slnx"),
        "<Solution />",
    )
    .unwrap();

    let nested = repo.join("ClashSharp").join("SandboxTest").join("src");
    fs::create_dir_all(&nested).unwrap();

    assert_eq!(find_repo_root(&nested), Some(repo.to_path_buf()));
}

#[test]
fn default_layout_places_sandbox_files_under_clashsharp_sandboxtest() {
    let repo_root = PathBuf::from(r"C:\src\ClashSharp");
    let layout = default_layout(repo_root.clone());

    assert_eq!(layout.repo_root, repo_root);
    assert_eq!(
        layout.artifacts_dir,
        PathBuf::from(r"C:\src\ClashSharp\artifacts")
    );
    assert_eq!(
        layout.sandbox_dir,
        PathBuf::from(r"C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox")
    );
    assert_eq!(
        layout.shared_dir,
        PathBuf::from(r"C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\shared")
    );
    assert_eq!(
        layout.wsb_file,
        PathBuf::from(r"C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\ClashSharpSandbox.wsb")
    );
}

#[test]
fn renders_wsb_with_shared_folder_and_logon_command() {
    let config = SandboxConfig {
        shared_dir: PathBuf::from(r"C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\shared"),
        logon_command: String::from(
            r"powershell.exe -ExecutionPolicy Bypass -File C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox\Run-InSandbox.ps1",
        ),
    };

    let wsb = config.render_wsb();

    assert!(wsb.contains("<MappedFolder>"));
    assert!(wsb.contains(
        r"<HostFolder>C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\shared</HostFolder>"
    ));
    assert!(wsb.contains("<ReadOnly>false</ReadOnly>"));
    assert!(wsb.contains("<LogonCommand>"));
    assert!(wsb.contains("Run-InSandbox.ps1"));
}

#[test]
fn dry_run_plan_describes_host_and_sandbox_next_steps() {
    let layout = default_layout(PathBuf::from(r"C:\src\ClashSharp"));

    let plan = render_dry_run_plan(&layout);

    assert!(plan.contains("ClashSharp Windows Sandbox test plan"));
    assert!(plan.contains("Default scenario: install-only"));
    assert!(plan.contains("Scenario selection: install-only, launch-no-proxy, startup-with-proxy-config, cleanup-uninstall, real-proxy-optional, all"));
    assert!(plan.contains("Host preparation"));
    assert!(plan.contains("Sandbox execution"));
    assert!(plan.contains(r"ClashSharp\SandboxTest\.sandbox\ClashSharpSandbox.wsb"));
}

#[test]
fn all_scenario_selection_expands_to_default_clean_environment_flows() {
    let scenarios = parse_scenario_selection("all").unwrap();

    assert_eq!(
        scenarios,
        vec![
            Scenario::InstallOnly,
            Scenario::LaunchNoProxy,
            Scenario::StartupWithProxyConfig,
            Scenario::CleanupUninstall,
        ]
    );
}

#[test]
fn comma_separated_scenario_selection_preserves_order() {
    let scenarios = parse_scenario_selection("install-only,cleanup-uninstall").unwrap();

    assert_eq!(
        scenarios,
        vec![Scenario::InstallOnly, Scenario::CleanupUninstall]
    );
}

#[test]
fn scenario_run_layout_uses_isolated_run_directory() {
    let layout = default_layout(PathBuf::from(r"C:\src\ClashSharp"));
    let run = layout.scenario_run("20260630T010203Z", Scenario::InstallOnly);

    assert_eq!(
        run.run_dir,
        PathBuf::from(
            r"C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\runs\20260630T010203Z\install-only"
        )
    );
    assert_eq!(run.shared_dir, run.run_dir.join("shared"));
    assert_eq!(run.payload_dir, run.shared_dir.join("payload"));
    assert_eq!(run.reports_dir, run.shared_dir.join("reports"));
    assert_eq!(
        run.scenario_plan_file,
        run.shared_dir.join("scenario-plan.json")
    );
    assert_eq!(
        run.wsb_file,
        run.run_dir.join("ClashSharpSandbox-install-only.wsb")
    );
}

#[test]
fn renders_scenario_wsb_with_isolated_shared_folder() {
    let layout = default_layout(PathBuf::from(r"C:\src\ClashSharp"));
    let run = layout.scenario_run("20260630T010203Z", Scenario::CleanupUninstall);
    let config = SandboxConfig {
        shared_dir: run.shared_dir,
        logon_command: String::from(
            r"powershell.exe -ExecutionPolicy Bypass -File C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox\scripts\Run-InSandbox.ps1",
        ),
    };

    let wsb = config.render_wsb();

    assert!(wsb.contains(
        r"<HostFolder>C:\src\ClashSharp\ClashSharp\SandboxTest\.sandbox\runs\20260630T010203Z\cleanup-uninstall\shared</HostFolder>"
    ));
    assert!(wsb.contains(r"scripts\Run-InSandbox.ps1"));
}

#[test]
fn scenario_plan_json_contains_payload_and_guest_paths() {
    let layout = default_layout(PathBuf::from(r"C:\src\ClashSharp"));
    let run = layout.scenario_run("20260630T010203Z", Scenario::InstallOnly);
    let plan = render_scenario_plan(
        &run,
        Path::new(r"C:\src\ClashSharp\ClashSharp\Installer\target\release\payload"),
    );

    assert!(plan.contains(r#""schemaVersion":1"#));
    assert!(plan.contains(r#""scenario":"install-only""#));
    assert!(plan.contains(
        r#""payloadSource":"C:\\src\\ClashSharp\\ClashSharp\\Installer\\target\\release\\payload""#
    ));
    assert!(plan.contains(
        r#""payloadPath":"C:\\Users\\WDAGUtilityAccount\\Desktop\\ClashSharpSandbox\\payload""#
    ));
    assert!(plan.contains(
        r#""reportsPath":"C:\\Users\\WDAGUtilityAccount\\Desktop\\ClashSharpSandbox\\reports""#
    ));
}

#[test]
fn report_validation_accepts_passed_report_for_expected_scenario() {
    let report = r#"{
        "schemaVersion": 1,
        "scenario": "install-only",
        "status": "passed",
        "steps": [],
        "checks": {
            "package": {
                "installed": true
            }
        }
    }"#;

    let summary = validate_report(Scenario::InstallOnly, report).unwrap();

    assert_eq!(summary.status, "passed");
}

#[test]
fn report_validation_rejects_failed_report() {
    let report = r#"{
        "schemaVersion": 1,
        "scenario": "install-only",
        "status": "failed",
        "steps": []
    }"#;

    assert!(validate_report(Scenario::InstallOnly, report).is_err());
}

struct TempFixture {
    root: PathBuf,
}

impl TempFixture {
    fn new(name: &str) -> Self {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!("clashsharp-sandbox-test-{name}-{unique}"));
        fs::create_dir_all(&root).unwrap();
        Self { root }
    }

    fn path(&self) -> &Path {
        &self.root
    }
}

impl Drop for TempFixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}
