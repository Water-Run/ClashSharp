use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use clashsharp_sandbox_test::paths::{default_layout, find_repo_root};
use clashsharp_sandbox_test::plan::render_dry_run_plan;
use clashsharp_sandbox_test::sandbox::SandboxConfig;

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
    assert!(plan.contains("Host preparation"));
    assert!(plan.contains("Sandbox execution"));
    assert!(plan.contains(r"ClashSharp\SandboxTest\.sandbox\ClashSharpSandbox.wsb"));
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
