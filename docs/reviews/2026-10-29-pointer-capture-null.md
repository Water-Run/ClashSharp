# 长按磁贴空捕获集合崩溃

恢复开发后先核对原工作区和远端 main：均为 `10c5197`，没有遗漏 Ds 的提交。原服务器桌面会话有效，应用与安装器均未运行；最新 38 文件与 `pointer-capture-20261028/before-native-latest` 备份逐项一致，继续保留 GW 已激活的状态。

沿用原 CI `40665055863` 和内部签名 `40668377894`，没有重跑。406 个 HTTP 范围全部核验后，完整生产归档 425128353 字节的 SHA-256 为 `a602ea01186cc8e154f853129e6315ee8cf262093a0478f5640aa6b6a43bcc2e`，与新读取的 GitHub 元数据一致。规范接收器继续核验四文件公开清单、原 ZIP、十文件身份、完整 CI 和临时签名材料清理。

原 ZIP 为 425121762 字节／`298da585586a000af23c1d6f7ab3777602e36f9a74b2535b79b12e25b575c62e`；身份摘要 `8f4c6485a2e721500e5775be6a501fd106aa4787b142eac7f5a9c0c8a6821d77`，Auth `89663AC7805632A923533A2A93BB6BE3644403E9`、MSIX `955DB4C28DE70480E57E428E99BEE7877E49F24E`。服务器只读载荷审计通过，收据 `503fdc4d5c6b0c2ac6da53fe81d62cb477c2f75560765d3d570516259d9193c6`。

匹配旧版安装器正常卸载进程 37480、新候选正常安装进程 74148，均完成并退出 0。实际安装文件、注册、服务及私有 SCM 关联审计通过，收据 `8b1b936712888d4e38792526a92882778a7459e7760a6c71440562bd7d96333e`。安装选择保留 startup/recovery/service/followSystem，MCP 关闭；首次注册启动 42704 正常退出 0。随后明确恢复原 38 文件，未使用历史快照。

## 发现与修正

实际注册启动 11756 后，在可见的被动磁贴上长按、移动触发崩溃，原生验收未通过。进程 76032 的同一手势诊断复现取得私有转储；转储仅留服务器私有目录，没有下载或上传。Windows 记录 `c000027b`，stowed HRESULT 为 `80004003`。ClrMD 从同一转储确认唯一 `System.ArgumentNullException` 的调用栈：

```text
System.Linq.ThrowHelper.ThrowArgumentNullException
System.Linq.Enumerable.Any
ClashSharp.View.MasterControl.BeginInfoTileDrag
DispatcherQueueTimer TypedEventHandler
```

尚未捕获指针时，实机 `InfoTileGrid.PointerCaptures` 返回 null。补丁先通过空条件运算检查已有捕获，再调用 `CapturePointer`，保留既有指针身份、取消、松开提交和 Ds 的样式、长按入口。没有增加仅匹配源码字符串的测试。

`ac500a9` 的服务器编译及主控 175 项回归通过；准确 CI `40811705911` 两作业成功，四归档和四份原始 TRX 独立核验 **7466／0／0**，CI 证据摘要 `729e51fd4122517ea1fd30c76cf814bf3eb3d15eb60393e6a9cc607419d36e89`。签名 `40814472646` 的生产及独立消费均成功；候选身份 `b5a3278f116f7be7e5a128083825c115e953f2cc3baf678de2c1ca24b3797271`、原 ZIP `6506f60efb373ed9bb17b4bb3cf448ebae966beccfeffff48b099e3c34787293`，42 文件保护后正常卸载 42372／0、安装 53112／0、首次启动 63492／0，并精确恢复这份最新数据。载荷和实际安装审计分别为 `22e78c6e92a468e4ec54f51848a01cf51b5aa548b0f86c1c76977a721f7c835f`、`abc404bbe4c08b419af56c354569043195a32e3d0fd2f014b7a0ffe7456f7ddf`。

## 原生交互与隐藏窗口边界

新候选注册启动 16516。最初仅调用 SetCursorPos 的移动观测未换位；私有只读进程快照确认长按和捕获仍存在。改用 SendInput 的真实移动事件后，同一安装和同一进程完成正向、逆向换位，三个当前可见磁贴的物理顺序恢复；没有再改产品或重跑 CI。原失败观测保留，不能归为产品换位失败。后续检查完整 18 磁贴持久化顺序。

Esc 已实际取消临时换位。使用传统 Win32 鼠标捕获查询的取消探针未适配此 XAML 输入站点，记录不具备取消通过的证明。改为最小化本应用窗口的实际边界测试后发现：恢复窗口再松手会提交临时顺序。读取受校验的设置载荷确认仅 MasterInfoTileLayout 改变，37 项 desired／待处理 0，其余值保持；这是另一个待修复的产品问题。

后续修正从 XamlRoot 的 ContentIslandEnvironment 取得实际顶层窗口，按页面生命周期成对订阅／解除 AppWindow.Changed；窗口隐藏或 OverlappedPresenter.Minimized 时取消手势并恢复已提交顺序，不依赖岛可见性通知。捕获丢失通过当前所有权判断，取消只忽略本次同步捕获交接，避免依赖 OriginalSource 必须等于 Grid 的假设。最终结果见下节；ac500 中的最小化失败记录保留。

## 最终准确候选与实机通过

最终源码 `57d8e38ae54890a3c1c34b597cd3dc6e1aabdcc4` 已推送，服务器源码摘要 `0463c8a84ea2ed2e77917300e477bd06efb4a04539ff711409fc54c4fbe73dd9` 在编译前后及本地提交间一致，175 项主控回归通过。中间 62ca8aa 的 CI 被后续完整修正取代并取消，没有将其当作通过证据。

准确 CI `40818208771` 两作业成功，四份原始 TRX 及四归档／开发包清单独立核验 **7466／0／0**（主测试4745、Core1141、Presentation206、Windows1374），证据摘要 `e3ff69cb6496c2d7f8fb24854d1c3a18956b550223f52ccfb2c102ed1cbc0968`。内部签名 `40820107636` 的生产和独立消费均成功，临时证书／私钥／预留槽／签名文件各2项清理核验。原生产归档12874726216为425129719字节／`c273a888366f151cc84395fa1520d3785ff4ea758fcca880e299c8efd2c9bb1b`。

最终候选身份 `817fb13feefceea19a9e46bd4140c0c92239cee88724a1adfb09ce50017bfe76`，原 ZIP425123128字节／`1af2e84b057638418e757593a8ecd43f4e5027d0f9d841068f8d295c0e5f3aa8`；Auth `103665E4CB9286B0534AE8A09C3EDC52C8485C52`、MSIX `B647727A13F8F1564E8EAAEC1669E2E99A43DC28`。服务器载荷审计收据 `6f5b7071b044766219df20d472e56d04c6b8638f3eab2c0ee0706e05c943b39d`，实际安装审计 `29211d5099a972eb2f60e449b2fec1335686aa790d25ef44c9869021c1c955e5`。

正常卸载34284／0、安装22776／0、首次启动64668／0，明确恢复最新39文件后注册启动69284。实际 SendInput 正向和逆向换位、Esc取消、最小化本窗口后恢复并取消、离页取消、随后Enter及UIA打开详情均通过。只操作被动磁贴，没有代理模式命令。页面滚动至首排磁贴的实图已查看；三个当时可见磁贴恢复原物理顺序，另以受校验设置载荷确认完整18磁贴顺序一致。37项desired均与操作前相同、待处理0；布局desired修订只增加2，恰好对应两次正常松手提交，三种取消没有持久化临时顺序。

69284通过设置中的正常退出命令退出0，启动和独立代理恢复仍为true。最后39文件逐项及完整实时清单核验通过，最新备份是 `C:\ClashSharpValidation\pointer-visibility-20261029\after-native-review`，清单摘要 `85815608845d6e2fd5b3a114a3fcfceec847ba6eb027e460bd50225f45350dc9`；不得再以更早的42／38文件快照覆盖。代理指纹 `c4cb7760f87d6a3ad3c80022f6747bbd78f8383916aa2476c087206c1abedc64` 保持。实际安装为 `1.0.0+57d8e38...`，主DLL `421f0f9f9a6d8724c485d5a2cf6cd5a3b567241ee5c10d27f200ad30332fb6bf`；服务Auto／Running／58168，产品与安装器进程0、事务无残留。

桌面 `Clash#.zip` 已替换为同一原 ZIP，`Clash#` 解压目录11文件逐项匹配，根文件恰为Setup.exe／LICENSE／README.md。原8fd ZIP与目录可逆保留。八项最终任务全部退出0后导出移除；旧60B／896／449公开Auth根按各自租约清理，当前1036公开根仅为后续安装器手验保留、无私钥，手验阶段结束且Setup全部退出后按租约移除。正常已安装MSIX信任和先前C0D／C3A信任保留。本轮旧传输二进制片段清理1265962721字节，原归档、收据及用户备份保留；最后C盘约91.9GiB。

43项不含用户原数据的证据已下载并逐成员核验，证据 ZIP `cc228cb8cad5eecbcbc7f558495624e510f2c14858d1ff1effebdd31a04193c1`、清单 `0a4cf180c5f73b5ef7f5152bae28dd93d5963294ca7c7462f0d8ffcdda83e6c8`。原私有崩溃转储仍只在服务器。验收仍使用原Server2025，没有增加重复Win11环境。此交互修复节点已通过，可接续用户的分项功能／样式手验；用户反馈、必要修复、最终确认和正式签名发布门槛仍保留，没有公开发布内部候选。

证据目录：`C:\ClashSharpValidation\pointer-capture-20261028` 和 `pointer-null-20261029`，异常摘要位于前者 `diagnostics/managed-exception-clrmd.json`。旧 60B Auth 公开根已按租约删除，7 个首轮自有任务已导出并移除。只对本次候选导入公开 Auth 根，独立租约记录所有权；没有导入私钥。正常 MSIX 信任和此前存在的测试信任保留。

参考：[WinUI PointerCaptures](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.pointercaptures)、[Microsoft ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)。
