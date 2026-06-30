use std::fmt;
use std::path::Path;

use serde_json::json;

use crate::paths::ScenarioRunLayout;

const GUEST_ROOT: &str = r"C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scenario {
    InstallOnly,
    LaunchNoProxy,
    StartupWithProxyConfig,
    CleanupUninstall,
    RealProxyOptional,
}

impl Scenario {
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::InstallOnly => "install-only",
            Self::LaunchNoProxy => "launch-no-proxy",
            Self::StartupWithProxyConfig => "startup-with-proxy-config",
            Self::CleanupUninstall => "cleanup-uninstall",
            Self::RealProxyOptional => "real-proxy-optional",
        }
    }
}

impl fmt::Display for Scenario {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.as_str())
    }
}

pub fn parse_scenario_selection(selection: &str) -> Result<Vec<Scenario>, String> {
    let trimmed = selection.trim();
    if trimmed.is_empty() || trimmed.eq_ignore_ascii_case("install-only") {
        return Ok(vec![Scenario::InstallOnly]);
    }

    if trimmed.eq_ignore_ascii_case("all") {
        return Ok(vec![
            Scenario::InstallOnly,
            Scenario::LaunchNoProxy,
            Scenario::StartupWithProxyConfig,
            Scenario::CleanupUninstall,
        ]);
    }

    trimmed
        .split(',')
        .map(|part| parse_scenario(part.trim()))
        .collect()
}

fn parse_scenario(value: &str) -> Result<Scenario, String> {
    match value {
        "install-only" => Ok(Scenario::InstallOnly),
        "launch-no-proxy" => Ok(Scenario::LaunchNoProxy),
        "startup-with-proxy-config" => Ok(Scenario::StartupWithProxyConfig),
        "cleanup-uninstall" => Ok(Scenario::CleanupUninstall),
        "real-proxy-optional" => Ok(Scenario::RealProxyOptional),
        other => Err(format!("unknown scenario '{other}'")),
    }
}

pub fn render_scenario_plan(run: &ScenarioRunLayout, payload_source: &Path) -> String {
    let plan = json!({
        "schemaVersion": 1,
        "scenario": run.scenario.as_str(),
        "runId": run.run_id,
        "payloadSource": payload_source,
        "paths": {
            "root": GUEST_ROOT,
            "payloadPath": format!(r"{GUEST_ROOT}\payload"),
            "reportsPath": format!(r"{GUEST_ROOT}\reports")
        }
    });

    serde_json::to_string(&plan).expect("scenario plan JSON should render")
}
