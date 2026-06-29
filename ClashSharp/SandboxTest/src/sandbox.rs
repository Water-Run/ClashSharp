use std::path::PathBuf;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SandboxConfig {
    pub shared_dir: PathBuf,
    pub logon_command: String,
}

impl SandboxConfig {
    pub fn render_wsb(&self) -> String {
        format!(
            concat!(
                "<Configuration>\n",
                "  <MappedFolders>\n",
                "    <MappedFolder>\n",
                "      <HostFolder>{}</HostFolder>\n",
                "      <SandboxFolder>C:\\Users\\WDAGUtilityAccount\\Desktop\\ClashSharpSandbox</SandboxFolder>\n",
                "      <ReadOnly>false</ReadOnly>\n",
                "    </MappedFolder>\n",
                "  </MappedFolders>\n",
                "  <LogonCommand>\n",
                "    <Command>{}</Command>\n",
                "  </LogonCommand>\n",
                "</Configuration>\n"
            ),
            escape_xml(&self.shared_dir.display().to_string()),
            escape_xml(&self.logon_command)
        )
    }
}

fn escape_xml(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
