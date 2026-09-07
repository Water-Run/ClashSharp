# WPF 单页安装器（1.0.0）

安装器采用简化的 VS Installer 布局：顶栏提供重新检查，正文只有一个 ClashSharp
产品卡片，底栏提供关闭。卡片显示版本、状态和当前可用操作；详细检查结果与可复制的
诊断代码放在默认收起的展开区。主程序继续使用 WinUI 3。

下面是实际 MainWindow XAML 的离屏渲染，运行状态由模拟端口提供，用于检查布局，
不代表这台电脑已经完成安装检查或安装。

![模拟未安装状态的单页布局](assets/installer-single-page-1.0.0.png)

![模拟安装任务的进度布局](assets/installer-single-page-progress-1.0.0.png)

## 状态与职责

| 状态 | 产品卡片操作 |
|---|---|
| 检查中 | 显示不定进度，可请求取消 |
| 未安装且检查通过 | 安装 |
| 已安装且检查通过 | 修复、卸载；不支持安装的平台仅保留经过验证的卸载 |
| 有准确的未完成事务 | 仅继续对应操作 |
| 操作进行中 | 显示步骤及进度，可请求取消 |
| 检查未通过 | 显示原因与诊断入口，不提供系统修改按钮 |
| 操作已完成 | 提供重新检查或关闭；卸载成功不会继续宣称应用已安装 |

页面不构造用户身份、包身份或提权请求。Presentation 只接收 IInstallerRuntime
的检查结果与操作进度；ProductionInstallerRuntime、Core 协议及 Windows
适配器继续负责系统检查和执行。此次修改没有调整允许操作集合或生产启用条件。

关闭按钮和窗口关闭事件共用既有取消与等待逻辑。任务仍在执行时保留窗口及运行端口，
等待任务完成后再释放资源并关闭。重新检查会清除上一操作的标题，避免显示过期状态。

## 验证

- 完整 Release x64 解决方案构建：18 项目，零警告、零错误。
- Installer Presentation 全部 97 项测试通过，零跳过。
- 实际编译后的 WPF MainWindow 与主题通过 45 项离屏断言，生成 27 张图，
  覆盖未安装、已安装、恢复、受阻、执行、完成和再次检查，以及展开、滚动和小窗口。
  检查内容包括按钮边界、诊断绑定、无 WPF 绑定错误和关闭时的取消后收尾。
- 图像内容区域为 884×561 或 764×481 逻辑单位，另有 192 DPI 栅格输出。
  栅格输出不是实际跨显示器 DPI 切换测试；此次没有打开生产窗口，也没有进行
  UIA、讲述人、系统安装、代理或服务修改。
- 完整 format 检查 1299 个源文件，零处变更、无工作区警告。

日志位于 artifacts/validation 下的 build-1.0.0-m4o-final.log、
test-1.0.0-m4o-presentation-final.log、format-1.0.0-m4o.log 和
render-1.0.0-m4o-final.log。脱敏收据
artifacts/verification/installer-shell-validation-m4o.json 记录三份程序集与每张图的
SHA-256、执行断言及验证范围。页面程序集摘要为
9bdf89971751a3bb575391c772546f6488ee94e1ebe5611f8fe8f6b06ef4ab61，
Presentation 摘要为 edf5f39e1d0f558057a95c8724e6eb54e314deb715ec9a273a61c0441b0d9f8a。

## 绿色入口与旧安装器清理

安装器是自包含 WPF EXE，无需预装 .NET。完整离线发行目录仍包含相邻 payload，
主程序包与系统服务仍需要安装；便携入口不等于系统组件免注册。

包含忽略目录在内的工作区没有 Rust 源码、Cargo 清单/锁文件、Rust 工具链或 Slint
页面，活动构建脚本也没有对应入口。清理了旧空 src、ui、tests 目录，保留品牌重建
文档引用的 Logo.png、LogoInstaller.png。清点收据为
artifacts/verification/rust-installer-inventory-m4o.json。

生产执行开关仍关闭。正式签名候选、Windows 11 实际安装及故障恢复验收继续按
[执行账本](../reviews/1.0.0-execution-ledger.md)推进；页面验证不能替代这些条件。
