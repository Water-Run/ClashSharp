# Windows 11 完整安装器的卷根权限缺口

准确 `8289dec` 的完整签名安装器在新建 Windows 11 x64 Sandbox、未安装 .NET 的账户中，于首次状态检查报告 `installer.transaction.root_ancestor_acl_invalid`，没有进入安装。原失败已保留，不能把启动安装器窗口计为安装通过，也没有修改沙箱或开发机 ACL 来绕过拒绝。

## 准确输入与原生失败

Installer SHA-256 为 `278180820F7530D56FC725AC7366E9B18F482CED0F674C35D899E2E723ADB47A`，四项最终公开载荷归档为 `2C849F964636367DDA983B972430383A048F02B0B53A17A77A87F94CD7C0F534`；MSIX 为 `1FCAD15E68DBFF98C10A4E2AF21E6FD0B2B3E22E61E8D4AEED3AD13E2C0A56C1`。Installer 与包的公开证书均核对，私钥没有导出。输入只读，宿主代理指纹在启动前保存；证书信任只在已核对身份的新客体中建立。

沙箱 ID `14ef8fea-253b-4415-849a-0fc28727b1b0`，客体 Windows 11 Enterprise 26100 x64，账户 `WDAGUtilityAccount`。网络与剪贴板、外设共享关闭，独立观察外部活动适配器为 0，`C:\Program Files\dotnet` 不存在。真实安装器进程 8116 在界面显示稳定拒绝码；独立快照确认没有应用包、服务、数据代、公共事务、包签名信任、代理日志、看门狗租约或端口监听，客体代理保持。

## 权限与代码对应

原生 ACL 记录的 `C:\` owner 为 SYSTEM，其 Authenticated Users 的有效 ACE 为 `0x1301BF`（Modify），包含对象自身的 `DELETE`。`C:\ProgramData` 同样由 SYSTEM 拥有；Users 的写 ACE 为 `0x116`，允许创建与写属性，未含 `DELETE_CHILD`。生产 `WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor` 把有效非受信 SID 的 `DELETE`、`DELETE_CHILD`、`WRITE_DAC`、`WRITE_OWNER` 和 GenericAll 均视为危险；交易、机器和服务目录链把卷根和可重命名祖先都送入同一规则。因此该客体在第一个卷根的安全观察就被拒绝。

下一步必须区分物理卷根与普通可重命名目录，并保留普通祖先、子项删除、权限改写、所有权接管、reparse、目录身份及不共享 DELETE 的持久句柄检查。不能仅凭 `C:\` 或 `Path.GetPathRoot` 字符串赋予例外，SUBST 或其他映射不构成物理卷根证明。微软的 [GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew) 定义了从现有句柄读取卷 GUID 路径的能力；据此取得可靠的根身份并验证文件系统语义，是后续实现方向，当前尚未作为修复通过。

共享策略的调用范围还包括目录创建、清理、目录台账、退役卸载和账户切换，修复需要逐项核对，不能只让首次检查通过而遗留服务或卸载拒绝。原生权限记录将成为回归输入；真实句柄的物理根/普通目录辨别以及安全负例也需要独立验证。

## 已完成收尾与后续

关闭未安装的正常 UI，客体再次确认产品进程、包、服务和事务均缺席，准确撤销本夹具导入的公开证书。随后停止刚创建的指定沙箱，CLI 独立确认其缺席，10 个输入文件摘要全部保持，宿主代理保持。12 项失败证据及清理核对全部通过，记录位于被 Git 忽略的 `artifacts/verification/windows11-installer-native-20261007/`。

## 句柄证明修复与本地验证

目录观察新增默认不成立的物理卷根标记。原生实现对同一个已持有的 SafeFileHandle 查询规范化卷 GUID 路径，仅接受完整 GUID 根，拒绝子目录、DOS 盘符、NT 名称、错误 GUID 和额外分隔符。查询不可用时不授予卷根例外；不依据调用者传入的盘符或路径推断根身份。字符缓冲区按 Unicode 原生契约传递，系统库查找限定 System32。

祖先策略只在该证明成立时不把对象自身的 `DELETE` 当作可重命名祖先授权；`DELETE_CHILD`、`WRITE_DAC`、`WRITE_OWNER`、GenericAll、未知 ACE、无 DACL 和不受信 owner 仍拒绝。普通目录沿用全部原检查。交易、机器部署、服务、目录台账、退役卸载和账户切换的祖先观察已接入；自建目录与删除目标保持原严格策略，不能把卷根识别扩展为可删除对象授权。没有修改任何系统 ACL。

捕获权限的交易回归先复现 1 项失败、24 项通过，再修复。新增 27 项测试覆盖完整 GUID 根解析、真实系统卷根与 System32 句柄区别、未证明盘符和普通祖先拒绝、危险授权与 owner/DACL 边界、机器根再次观察时失去证明、账户切换不改卷根 ACL，以及服务读取。84 项 Installer 相关集、22 项服务相关集、本地主程序全量 4,028 项和 Windows 可执行集 1,310 项全部通过，零失败、零跳过。13 项隔离原生用例仍交给真实 CI 环境；没有伪造 CI 条件或计为本地通过。18 项目 Release 构建零警告、零错误，完整格式通过。

首次编译触发字符缓冲区分析规则后修正，另一项新增测试的内部枚举参数可访问性问题也已修正；原失败检查保留。下一节点重新构建准确签名候选，完成 Windows 11 的安装、正常启动、同内容修复与卸载。当前不宣称这些实机路径已通过。原候选的 Server 验收和 CI 6,601 项证据保留；G08 与完整生产目标继续，版本仍为 `1.0.0.0`，正式签名按既有选择随后配置。
