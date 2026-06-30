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
            "- Default scenario: install-only\n",
            "- Scenario selection: install-only, launch-no-proxy, startup-with-proxy-config, cleanup-uninstall, real-proxy-optional, all\n",
            "\n",
            "Sandbox execution\n",
            "- Map each scenario shared directory into the Sandbox desktop.\n",
            "- Run scripts/Run-InSandbox.ps1 inside Windows Sandbox.\n",
            "- Install ClashSharp from the copied payload and export structured reports.\n",
            "\n",
            "Dry run only: pass -Launch to Run-SandboxTest.ps1 when the Sandbox test is ready.\n"
        ),
        layout.repo_root.display(),
        layout.artifacts_dir.display(),
        layout.sandbox_dir.display(),
        layout.wsb_file.display()
    )
}
