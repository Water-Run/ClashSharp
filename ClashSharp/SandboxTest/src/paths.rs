use std::path::{Path, PathBuf};

use crate::scenario::Scenario;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SandboxLayout {
    pub repo_root: PathBuf,
    pub artifacts_dir: PathBuf,
    pub sandbox_dir: PathBuf,
    pub shared_dir: PathBuf,
    pub wsb_file: PathBuf,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScenarioRunLayout {
    pub scenario: Scenario,
    pub run_id: String,
    pub run_dir: PathBuf,
    pub shared_dir: PathBuf,
    pub scripts_dir: PathBuf,
    pub payload_dir: PathBuf,
    pub reports_dir: PathBuf,
    pub scenario_plan_file: PathBuf,
    pub wsb_file: PathBuf,
}

impl SandboxLayout {
    pub fn scenario_run(&self, run_id: &str, scenario: Scenario) -> ScenarioRunLayout {
        let run_dir = self
            .sandbox_dir
            .join("runs")
            .join(run_id)
            .join(scenario.as_str());
        let shared_dir = run_dir.join("shared");
        let scripts_dir = shared_dir.join("scripts");
        let payload_dir = shared_dir.join("payload");
        let reports_dir = shared_dir.join("reports");
        let scenario_plan_file = shared_dir.join("scenario-plan.json");
        let wsb_file = run_dir.join(format!("ClashSharpSandbox-{}.wsb", scenario.as_str()));

        ScenarioRunLayout {
            scenario,
            run_id: run_id.to_string(),
            run_dir,
            shared_dir,
            scripts_dir,
            payload_dir,
            reports_dir,
            scenario_plan_file,
            wsb_file,
        }
    }
}

pub fn find_repo_root(start: &Path) -> Option<PathBuf> {
    let mut current = if start.is_file() {
        start.parent()?.to_path_buf()
    } else {
        start.to_path_buf()
    };

    loop {
        if current.join("README.md").is_file()
            && current.join("ClashSharp").join("ClashSharp.slnx").is_file()
        {
            return Some(current);
        }

        if !current.pop() {
            return None;
        }
    }
}

pub fn default_layout(repo_root: PathBuf) -> SandboxLayout {
    let artifacts_dir = repo_root.join("artifacts");
    let sandbox_dir = repo_root
        .join("ClashSharp")
        .join("SandboxTest")
        .join(".sandbox");
    let shared_dir = sandbox_dir.join("shared");
    let wsb_file = sandbox_dir.join("ClashSharpSandbox.wsb");

    SandboxLayout {
        artifacts_dir,
        sandbox_dir,
        shared_dir,
        wsb_file,
        repo_root,
    }
}
