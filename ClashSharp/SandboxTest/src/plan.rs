use crate::paths::SandboxLayout;

pub fn render_dry_run_plan(layout: &SandboxLayout) -> String {
    format!(
        concat!(
            "ClashSharp Windows Sandbox test plan\n",
            "\n",
            "Host preparation\n",
            "- Repository root: {}\n",
            "- Artifact input directory: {}\n",
            "- Sandbox working directory: {}\n",
            "- WSB output: {}\n",
            "\n",
            "Sandbox execution\n",
            "- Map the shared directory into the Sandbox desktop.\n",
            "- Run scripts/Run-InSandbox.ps1 inside Windows Sandbox.\n",
            "- Later steps can install ClashSharp, exercise the UI/service, and export logs.\n",
            "\n",
            "Dry run only: pass -Launch to Run-SandboxTest.ps1 when the Sandbox test is ready.\n"
        ),
        layout.repo_root.display(),
        layout.artifacts_dir.display(),
        layout.sandbox_dir.display(),
        layout.wsb_file.display()
    )
}
