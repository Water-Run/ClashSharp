use serde_json::Value;

use crate::scenario::Scenario;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReportSummary {
    pub status: String,
}

pub fn validate_report(expected_scenario: Scenario, text: &str) -> Result<ReportSummary, String> {
    let value: Value =
        serde_json::from_str(text).map_err(|error| format!("report is not valid JSON: {error}"))?;

    let schema_version = value
        .get("schemaVersion")
        .and_then(Value::as_i64)
        .ok_or_else(|| String::from("report missing numeric schemaVersion"))?;
    if schema_version != 1 {
        return Err(format!("unsupported report schemaVersion {schema_version}"));
    }

    let scenario = value
        .get("scenario")
        .and_then(Value::as_str)
        .ok_or_else(|| String::from("report missing scenario"))?;
    if scenario != expected_scenario.as_str() {
        return Err(format!(
            "report scenario mismatch: expected {}, got {scenario}",
            expected_scenario.as_str()
        ));
    }

    let status = value
        .get("status")
        .and_then(Value::as_str)
        .ok_or_else(|| String::from("report missing status"))?;

    match status {
        "passed" | "skipped" => Ok(ReportSummary {
            status: status.to_string(),
        }),
        "failed" | "timedOut" => Err(format!("scenario {scenario} ended with status {status}")),
        other => Err(format!("unknown report status '{other}'")),
    }
}
