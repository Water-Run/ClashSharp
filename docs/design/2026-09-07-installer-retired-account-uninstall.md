# 迁移后旧账户的独立卸载

旧账户卸载自己的安装时，共享服务已经可能属于新账户。因此，这条流程必须使用独立的认证入口和恢复记录，并且不持有共享服务、关联文件或共享载荷的修改能力。普通卸载事务和 AllowReassociation 不提供这项权限。

## M4u 已实现的证书清理

迁移时保存的旧账户证书可能与当前候选包的签名证书不同。[InstallerArchivedCertificateRemoval](../../ClashSharp/ClashSharp.Installer.Core/Certificates/InstallerArchivedCertificateRemoval.cs)直接使用私有归档里的旧身份，不制造另一个已验证 release，也不借用当前候选包的证书身份授权删除。它只接收归档存储、精确检查/删除端口和独立卸载边界验证器，没有证书导入或机器修改接口。

执行顺序为：验证旧账户及包缺席等前提，读取准确归档，检查受管证书是否存在身份冲突，持久释放唯一引用，再精确删除安装器曾导入的证书。确认删除完成且边界仍然成立后，才清除引用为零的准确归档。预先存在的证书不打开其存储进行检查或修改；归档缺失时没有可授权删除的证书身份。

[InstallerArchivedCertificateStore](../../ClashSharp/ClashSharp.Installer.Core/Certificates/InstallerArchivedCertificateStore.cs)只允许读取、释放现有引用和清除已释放记录，不允许创建归属、接管证书或增加引用。它要求规范 JSON、准确 SID、完整快照和内容摘要；写入或删除确认丢失时，持有调用者的权限和句柄，使用不受取消影响的读取观察实际结果。冲突、未知格式和无法观察的结果均保留证据。证书操作失败或取消时，已释放引用的归档仍可供重试。

Windows 持久层只使用既有 InstallerAuthority/v1 下按 SID 摘要固定命名的归档。只读根验证器不会创建目录，也不修改现有 ACL；每次文件操作都会重新验证被固定的目录链。原生文件端口为每种用途绑定唯一叶名称和大小上限，普通迁移日志实例仍拒绝归档文件，归档实例拒绝其他账户、活动账本、普通日志、临时文件和备用数据流。文件必须是单链接普通文件，具有准确的 SYSTEM/Administrators 私有权限。

[WindowsArchivedCertificateRemovalAdapter](../../ClashSharp/ClashSharp.Installer.Windows/Certificates/WindowsArchivedCertificateRemovalAdapter.cs)固定使用调用者认证所确定的目标 SID/TrustedPeople。删除同时匹配 SHA-1 指纹和完整 DER SHA-256，并要求 InstallerOwned 为真、持久引用为零。它不以替代管理员的 CurrentUser 作为目标，不接受载荷或导入请求，不创建证书存储。Windows 释放验证与普通证书适配器共用同一精确身份比较算法。

## 验证范围

新增 95 项测试：Core 54 项，Windows 41 项。完整本机安全测试 4168 项通过、零失败、零跳过：主程序 2330、Core 876、Presentation 109、Windows 853。会修改真实当前用户证书存储的三个既有测试继续只在隔离 CI 执行。18 项目 Release x64 构建零警告、零错误。

隔离 Windows 的两个进程通过 13 个场景、84 项原生断言：真实目标用户证书删除、预先存在证书与无关证书保留、已释放/已删除状态重放、缺失私有根、错误 SID、非规范记录、硬链接、用户可读文件、目录冒充文件、权限晚变，以及写确认丢失后新进程恢复。生产适配器没有创建目录或证书存储；102 个目录打开和 14 个证书存储打开均释放。四张一次性测试证书清理后，TrustedPeople 的完整内容摘要与测试前一致；独立清理脚本再次确认零测试证书、零探针进程，随后移除隔离测试树。

原生探针使用的 Windows 程序集 SHA-256 为 1a0d06013fd697287099bc9382b4824d88a082a0b28307c751e03f0d34e07d4a，Core 为 4017192a735b4233bc4b6358a12398751b997e70351131179ae8e931f2a19916。它们是本阶段源代码的提交前构建，源链接基线为 7459de5；不等同于后续 CI 包中的程序集。原始收据为 artifacts/validation/archived-certificate-m4u-{run,resume,cleanup}.json，汇总为 artifacts/verification/archived-certificate-validation-m4u.json。

## 继续实现

本阶段交付归档证书清理能力，尚未连接旧账户独立卸载的生产入口。IInstallerArchivedCertificateRemovalBoundary 的生产实现仍需由专用 helper 提供：连续持有机器排他、旧账户 App 屏障及候选包，独立验证包缺席、共享安装属于另一个账户，并处理迁移或普通安装尚未完成时的拒绝与恢复。Windows 原生探针中的这一边界使用模拟前提，因此不计作实际包卸载、双账户认证或共享服务保持验收。

接下来接入独立会话、私有卸载恢复记录和单页 WPF 操作，验证标准用户、替代管理员、共享安装变化及中断恢复。生产执行门保持关闭，仍需完成正式签名候选包在 Windows 11 上的完整安装、修复、升级和卸载矩阵。
