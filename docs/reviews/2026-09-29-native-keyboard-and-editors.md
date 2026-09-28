# 原生键盘交互与编辑器样式验收

全部 GUI、安装和网络状态验证均在指定蓝色服务器 `WIN-07OBU2PV8C6` 的验收账户完成。开发机仅用于源码、构建、报告和证据核对。本记录使用 UTC 时间；对应北京时间为 9 月 29 日凌晨。

## 6b4bddb：窗口菜单与控件键盘输入

f8965db 中，语言下拉框保有焦点时，Alt+Space 同时打开 Windows 窗口菜单和下拉列表。6b4bddb 在窗口内容的 `PreviewKeyDown` 中仅拦截带 Alt 的空格键，保留系统窗口菜单与其他原生按键行为。处理方式参考 [Microsoft 键盘事件](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-events)及[预览键盘事件说明](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators)。

服务器于 16:52 UTC 构建成功。f8965db 正常卸载后，16:55 UTC 核对包、服务及六处自有安装目录均已移除；16:56 UTC 通过原生安装器正常安装 6b4bddb。主程序集与 MSIX 载荷 SHA-256 均为 `E8E6B2BE15411DEB469209DC076161E6F5CAE3329A46CE6E7D627877DB793DB3`，仅注册给指定验收 SID。安装后的安装器、MSIX、Windows App Runtime 签名全部有效，仍为验收证书方案。

| 实机场景 | 结果 |
| --- | --- |
| 中文浅色普通窗口，语言下拉框有焦点 | Alt+Space 只打开系统窗口菜单；Esc 关闭菜单 |
| 返回控件焦点后，普通空格、Enter、Alt+↓ | 均可展开下拉框；Enter 提交，Esc 取消，选项及原生焦点框正常 |
| 下拉框滚出可视区 | Alt+Space 不再弹出屏幕外的下拉列表；回到顶部后语言仍为自动检测 |
| URL 编辑按钮在取消对话框后保有焦点 | Alt+Space 不会重新打开编辑框 |
| 中文深色最小 800×600 窗口 | 语言选项完整，焦点框、蓝色还原链接、设置文字正常；Alt+Space 只显示系统窗口菜单 |
| 活动服务/TUN 的长状态文字 | 完整显示“Mihomo 服务已部署并正在运行。”，没有末字单独换行；补齐 f8965db 状态宽度修复的实机证据 |
| 活动 TUN 下从设置正常退出 | 17:15:46 UTC 退出；17:16 UTC 应用、内核、看门狗及 TUN 均为空，系统代理关闭，期望与实际模式均为 Disabled |

滚轮复查曾出现语言变化，但独立读取服务器 `GetCursorPos` 确认：Linux 端只移动指针后，Windows 指针仍在原点击位置 `(1110,252)`，没有移动到指定空白区。因此这次尝试属于远程输入偏差，不计产品缺陷或通过证据。改为实际点击空白区再滚动后，语言不变、没有待重启标记，Alt+Space 的屏幕外场景正常。原始失败尝试和指针观测均保留；后续验收不能仅凭 Linux 端 `move` 成功认定 Windows 指针已移动。

闭合备份 `closed-state-20260928-1716` 含 17 文件、346,684 字节，逐项散列校验。两个数据库完整性正常，43 条日志中无 Warning/Error。此前原始 42/44 文件业务夹具继续保留；本轮没有恢复原始夹具，不计自动迁移测试。

本地 18 项目 Release x64 构建及格式检查通过，零警告、零错误。[CI 36453324246](https://github.com/Water-Run/ClashSharp/actions/runs/36453324246) 两项作业成功，下载四份 TRX 独立核对：主程序 3,352、安装器核心 1,031、安装器界面 152、Windows 适配 1,126，合计 **5,661 通过，零失败、零跳过**。

| 证据归档 | SHA-256 |
| --- | --- |
| `evidence-6b4bddb-native-keyboard.zip`，13 份 JSON | `D475B43F1A1FB92B9FAEAB6CB84883CD4382D298111B658EC1B1B339D7EE4F66` |
| `desktop-evidence-native-keyboard-20260928T171718Z.tar.gz`，59 张截图及操作记录 | `A3BF7E3C507034307CC59CFD834CD07C2A65D2E9B65CF7073D2CB24094780583` |

归档及 `native-keyboard-verification.json`、`ci-6b4bddb-verification.json` 位于忽略目录 `artifacts/verification/server-acceptance-20260922`。`Verify-6b4bddb-Native.py` 和 `Verify-6b4bddb-CI.py` 均执行通过。全功能矩阵保持 **84 通过、37 部分、20 待测**；没有把几个键盘场景等同于全页面、全语言和全 DPI 验收。

## 182efc2：编辑器的文本链接与对齐

URL 编辑框内的“还原”和磁贴编辑器的“恢复推荐布局”改用原生 `HyperlinkButton`，与设置页的次要操作保持一致。URL 输入框取消固定居中宽度，随内容区拉伸并与标签对齐；长标签允许完整换行。保存、取消及草稿恢复逻辑保留。

18 项目 Release x64 构建和格式检查通过，零警告、零错误。[CI 36457079141](https://github.com/Water-Run/ClashSharp/actions/runs/36457079141) 两项作业成功；四份 TRX 独立核对仍为 **5,661 通过，零失败、零跳过**。服务器 17:25 UTC 构建完成，正常卸载 6b4bddb 后核对包、服务及六处自有目录均已移除，17:30 UTC 通过原生安装器安装新候选。

安装 DLL 与 MSIX 载荷散列一致，为 `9EBCFBE7692D1BA31A168CCB1A90C294A6ED8F822FDE5A0DC4E45AC898FB2630`，仅注册给验收账户。17:31 UTC 重新查询的安装器、MSIX 与 Windows App Runtime 签名均为 Valid，仍使用验收证书。

| 实机场景 | 结果 |
| --- | --- |
| 中文浅色普通窗口的 URL 编辑器 | 标签与输入框对齐，恢复为蓝色文本链接；Tab 可达，有系统焦点框，Enter 可还原 |
| URL 草稿取消和保存 | 先保存自定义 URL；还原后取消仍保留自定义值，再还原并保存后使用三项默认 URL |
| 中文浅色磁贴编辑器 | 隐藏全部并保存得到空主页；恢复推荐后取消仍为空；键盘 Shift+Tab 到链接、Space 恢复 20/68 项，保存后显示推荐磁贴 |
| 应用重启后的持久化 | 原生重启后推荐选择与三项默认 URL 均保留 |
| 德文深色 800×600 | 两个编辑器标题、标签、长恢复链接和底部按钮完整显示；磁贴列表独立滚动；链接采用系统强调色和白色焦点框 |
| URL 校验与恢复 | `https://` 因缺少主机被拒绝，原生错误提示完整显示并聚焦错误字段；Space 还原默认值后清除错误，再保存成功 |
| 链接悬停与离开 | Windows 会话内 `SendInput` 配合实际指针坐标和逐步截图，确认两个链接悬停底色出现、移开后消失 |

`not-a-url` 被规范化为 `https://not-a-url` 是现有单标签主机处理，并非校验失败；本轮没有请求该地址。远程 Linux 指针移动和单独 `SetCursorPos` 不足以证明 Windows 控件收到悬停输入。首次移动无视觉响应的尝试保留在归档中，不计通过；后续由受限于验收用户、会话、前台应用及窗口范围的原生输入和截图共同确认。输入依据为 [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) 与 [MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)。

17:58 UTC 从设置页正常退出；应用、内核、看门狗及 TUN 均为空，系统代理关闭，期望与实际模式均为 Disabled、代际一致。两个数据库完整，73 条日志中无 Warning/Error；本轮内核未启动，没有流量或连接样本。闭合备份 `closed-state-20260928-1758` 含 13 文件、264,921 字节，逐文件散列核对，验收账户保留德文深色设置。

| 证据归档 | SHA-256 |
| --- | --- |
| `evidence-182efc2-native-editors.zip`，19 份 JSON | `8F85AFAFABECC5D06B2D3344B5725276D8DA783379B35295D30AE9527EE9C261` |
| `desktop-evidence-native-editors-20260928T175922Z.tar.gz`，83 张截图及对应操作记录，包含旧版卸载 | `A1B9168AD561F592B07A6A02A4F371387154C6783CA7A33C2D8C8D3236C578CE` |

归档及 `native-editors-verification.json`、`ci-182efc2-verification.json` 位于同一忽略证据目录，`Verify-182efc2-Native.py` 与 `Verify-182efc2-CI.py` 均通过。全功能矩阵保持 **84 通过、37 部分、20 待测**；这些编辑器场景不代表全部 68 张磁贴的活动、空值与错误状态通过。其他语言/主题/DPI、设置代际整体切换、安装故障/取消路径及正式发行验证继续保留为未完成项。
