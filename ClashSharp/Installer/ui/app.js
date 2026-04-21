const installDirInput = document.getElementById("installDir");
const installBtn = document.getElementById("installBtn");
const progressBar = document.getElementById("progressBar");
const percentText = document.getElementById("percentText");
const statusText = document.getElementById("statusText");

function setProgress(percent, text) {
  progressBar.style.width = `${percent}%`;
  percentText.textContent = `${percent}%`;
  statusText.textContent = text;
}

window.installer = {
  onProgress(payload) {
    setProgress(payload.percent ?? 0, payload.text ?? "处理中");
  },
  onDone(payload) {
    setProgress(100, `安装完成：${payload.install_dir}`);
    installBtn.disabled = false;
    installBtn.textContent = "安装完成";
  },
  onError(payload) {
    statusText.textContent = `错误：${payload.message}`;
    installBtn.disabled = false;
    installBtn.textContent = "重新安装";
  }
};

async function bootstrap() {
  const state = await window.pywebview.api.get_initial_state();
  installDirInput.value = state.default_install_dir;

  if (!state.payload_exists) {
    statusText.textContent = "警告：payload 目录不存在";
  }
}

installBtn.addEventListener("click", async () => {
  const installDir = installDirInput.value.trim();
  if (!installDir) {
    statusText.textContent = "请填写安装目录";
    return;
  }

  installBtn.disabled = true;
  installBtn.textContent = "安装中...";
  setProgress(0, "开始安装");

  await window.pywebview.api.start_install(installDir);
});

window.addEventListener("pywebviewready", bootstrap);