# 固定候选的服务器安装、原数据恢复与原生退出

## 准确CI与候选

签名身份工具提交95bfd054af516c304bb58631dc1b765df646903a的[完整CI40026562589](https://github.com/Water-Run/ClashSharp/actions/runs/40026562589)成功，四份原始TRX在服务器再次核验7,263／0／0；GitHub报告归档SHA256为`3AC545AEB93850B47A724845BE0352294CEB931C042787B886030785D89411A1`。

随后带租约消费及载荷执行工具提交94638fb590d83cc78930050c918c8849d314fe35的[完整CI40028743000](https://github.com/Water-Run/ClashSharp/actions/runs/40028743000)两项job均成功。两份scope准确指向94638fb、product=true；新增契约步骤确实执行。四份原始报告经服务器TestResultEvidence核验 **7,263通过／0失败／0跳过**，原始GitHub报告归档SHA256为`C93316F70697C90559F1EAEBA2BBAB1A266604C1DF9F24906D2D77FE0B0A8D34`。没有把scope中的执行前分类字段当作测试结果。

本轮安装复用先前实际生产并封存的e4caee891d7aa42a35f9285e1281de6b7f0a5de9候选，独立身份为：

`ad19068277ccc4ab381d83bed709cb39b7a24173a738bbca9defc0918b46d8ca`

其生产来源CI仍是准确e4caee8／40022979388。后续PS工具CI没有被替换为候选的来源CI，也没有将该包改名为94638fb编译产物。Git核对ebbbb65至e4caee8的UI、Core、Application、Infrastructure、MCP、服务、看门狗及Installer三层生产目录无差异，新增内容是发行工具与审查记录；仍按准确签名字节记录本次安装。

## 实际安装与关闭数据

在原Administrator／session1执行。先新建受保护的47文件备份并逐项与原关闭数据匹配。使用原ebbbb65正常安装器卸载，PID14216正常关闭退出0；已卸载状态下将尚存的一个原数据文件可逆保留到本节点私有目录，其余原文件已在完整备份中。随后通过e4候选的正常安装器安装，PID308正常关闭退出0。候选清单与八个输出在安装消费期间保持读取租约，操作后复验全部字节。

安装主程序集SHA256 `EB26EDEB1A76FB445941C20FE59B1B32ABC9BF21BB9566CB9DAA0E9CC080BFE9`，服务程序SHA256 `F6748F9C9FDFC2D29BBE838581E572C562E13CEF759E2E0C834224D4C59FCD0E`，均与该准确MSIX内容匹配。以备份显式恢复47个原文件，未冒称自动迁移。服务恢复Auto／Running，权限与原值一致；原代理指纹不变；证书多重集8／4／2／42保持，两个安装阶段的自有临时Root均已移除，没有导出新PFX。

六张760×520安装器实际画面逐一查看，状态、说明、底部命令和滚动区未见文字溢出。首轮辅助截图目录仍绑定旧节点，在发出卸载命令前拒绝，PID10016退出0、原47文件及原安装不变；修正节点目录绑定后才执行上述实际卸载。第一次恢复因旧Windows库存路径被错误改为规范斜杠，在复制前拒绝；确认MSIX摘要未变后，按准确库存路径规范化修正并完成恢复。失败没有覆盖为通过。

## 原账户启动与真实托盘退出

第一次正常AppsFolder激活得到PID3012；过早读取进程路径的观察守卫中断后，核对同一PID、创建时间和安装路径继续观察，没有重启。实际WinUI主窗及启动检查可读，1920×1032可见DWM帧已查看：启动检查“正常”列与操作对齐，主页左侧大卡片、右侧四项及直角磁贴布局正常。该帧不构成最小尺寸、DPI或Win11全矩阵证明。

窗口关闭实际按当前设置最小化到托盘。辅助脚本误把保持运行当成退出超时，后续只读观察确认原窗口隐藏、原进程仍在。Win32托盘菜单没有暴露所期待的UIA条目；发送键盘输入也没有形成可靠的退出证据，均保留失败。通过[GetMenuBarInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmenubarinfo)和[GetMenuItemRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmenuitemrect)核对原生菜单、enabled命令1301、实际边界及高亮后，以指针选择“安全退出”，PID3012与看门狗均终止。但该观察器未预先保持内核句柄，退出码缺失，仍不计为0。

原47文件恢复后补验一次：PID7060在任何UI动作前打开并保持原生进程句柄，实际主窗就绪、关闭启动检查，再选择实际托盘命令1301。WaitForSingleObject观察终止后，以同一保留句柄GetExitCodeProcess取得 **退出码0**；运行子进程退出、指针位置恢复。没有通过修改关闭偏好绕过托盘，也没有强杀进程。两次补充任务在激活前被Windows PowerShell5.1长路径哈希限制拒绝，改为扩展路径读取及同一文件流SHA256后才执行最终成功实例；没有放宽原47文件摘要守卫。

## 最终状态与证据

两次实际应用启动产生四份新的运行配置缓存，仅将这些不在原47文件库存中的文件移入受限私有目录，逐文件验证可逆移动。原数据文件未移动或删除，配置正文不导出。最终再次显式恢复并核对原47文件，最新关闭备份为服务器`candidate-platform-20261024/after-native-review`。

服务Auto／Running／PID6180，主程序及服务程序准确匹配e4候选；原代理、服务权限及证书集合保持；待处理事务0、产品进程0。16个本节点任务的成功／失败状态及XML均归档后移除，没有处理其他用户任务。C盘可用63.46GiB。本轮没有本机产品测试或Sandbox。

规范平台案例引用该独立候选及六份原始结果摘要，实际SHA256为：

`4D4894C524D775C9CBEF05C64A7CA78CF58AF1B4642C3EAFA79637CCB83890F6`

允许列表证据归档98项、97文件逐一核验，ZIP SHA256 `DEE4F0C8CFB99480FEF714EDFD6497B6B10024099C8549205FB24641A4B9581C`。本地为`artifacts/verification/server-candidate-platform-20261024/platform-evidence.zip`；服务器接续点为同节点`checkpoint.json`。包中不含原数据文件、四份生成配置正文或私钥，截图和辅助脚本留在私有验收证据中。

此案例只证明Windows Server10.0.26100／ProductType3／X64、原管理员的正常安装、显式备份恢复和原生启动／退出。完整故障恢复、实际Win11、不同身份及受保护的同摘要发布仍未放行，promotionAuthorized=false，整体目标继续。
