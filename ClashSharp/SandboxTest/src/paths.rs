use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SandboxLayout {
    pub repo_root: PathBuf,
    pub artifacts_dir: PathBuf,
    pub sandbox_dir: PathBuf,
    pub shared_dir: PathBuf,
    pub wsb_file: PathBuf,
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
