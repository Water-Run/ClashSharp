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

当前修正的准确 CI、签名候选和原生正向／逆向拖动、Esc／捕获丢失／离页取消及后续键盘／UIA 激活仍待完成。已安装的 `1f0e769` 不能标为验收通过；桌面手验包尚未替换成该有缺陷候选。用户手验及正式发布也未完成。

证据目录：`C:\ClashSharpValidation\pointer-capture-20261028`，异常摘要位于 `diagnostics/managed-exception-clrmd.json`。只对本次候选导入公开 Auth 根，独立租约记录所有权；没有导入私钥。正常 MSIX 信任和此前存在的测试信任保留。

参考：[WinUI PointerCaptures](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.pointercaptures)、[Microsoft ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)。
