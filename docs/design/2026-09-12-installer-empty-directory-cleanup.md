# 安装器空目录终结协议

**暂停检查点（2026-09-12）**：用户要求收尾并暂停。本文件及同分支的目录归属、原生删除、terminal store 与相关新测试属于未完成工作，单独保存，不包含在本次 main `2c0266b` 源码节点中。完整 Windows 装配、统一编译测试、崩溃恢复矩阵及服务器真实自动清理均未完成，不能将下述设计描述视为已验收功能。已经完成的 reader 句柄寿命、结果协议、Clear 回复前扩展点和 WPF 展示分别在 `96e9cf7`、`2c0266b`。

恢复时先核对本分支未验证源码，接通 protected stores、普通 helper 的 terminal finalizer、parent reader fallback，以及机器根创建归属；再统一执行 Windows 测试和隔离 native 探针。只有实际安装/修复/卸载及清理中断恢复通过后，才能把自动空目录清理计为产品能力。旧账户独立卸载仍须单独评估。

本批次仅清理安装器可证明创建、身份仍相同且为空的固定目录。既有版本只有目录安全验证，没有公共父目录创建记录；旧目录不补猜归属。

## 固定能力

六个角色由本机 ProgramFiles/CommonApplicationData known-folder 派生，不从 journal、命令行或 JSON 接受路径：ProgramFilesProduct、ProgramDataProduct、InstallerRoot、InstallerVersion、AuthorityRoot、AuthorityVersion。删除顺序为程序文件 product、InstallerVersion→InstallerRoot、AuthorityVersion→AuthorityRoot、ProgramDataProduct。Service/MihomoService 仍沿用原有机器资源删除和最终验证。

唯一归属/终结 ledger 是 CommonApplicationData 根下的 `ClashSharp.InstallerDirectories.v1.json`，位于六个待删目录外。不引入新目录、ADS、注册表存储或产品公开测试入口。新文件使用保护 DACL：Administrators/SYSTEM 全权、BUILTIN Users 只读，owner 为 Administrators；内容仅目录角色、volume/file ID 和既有非秘密事务字段。

每次读写都 pin known-folder 路径链，验证 ordinary/no-reparse/trusted rename-anchor；文件必须 ordinary、单 hardlink、owner/exact DACL 符合新协议。外来同名文件、未知 JSON 字段、重复角色、无效大小、损坏或不一致的事务证据均拒绝，不修 ACL、不接管、不把错误折成 missing。

## 创建归属

目录创建必须使用可区分“本调用新建成功”和“已存在”的原生创建结果。只有前者在持有新目录 guard 时取得 volume/file ID 并持久记录；已有目录绝不添加归属。记录中的角色必须来自固定派生路径。

创建成功后、归属 ledger 持久提交前发生崩溃，该目录之后属于无持久证据：保留并返回 RetainedUnprovenOwnership。预写的路径、目录名称、符合安全 ACL、同一目标 SID 都不能替代创建后记录的对象身份。此安全边界不承诺在该断点仍无目录残留。

记录使用有界 canonical 序列化、写穿透并 flush 的同目录原子提交模式，沿用现有 private journal 的保护文件创建方式；首次发布禁止覆盖，只有已认证现有文件才能原子替换。Load 拒绝非 canonical 字节，保持后续 bytes CAS 一致。发生未知写入结果时停止当前操作并重新读取真实 ledger；不重复外围安装事务。只清理由当前调用确知新建且身份匹配的临时文件；不扫描删除名称相似的外来文件。

在随机临时 ledger 创建后、原子 rename 前发生进程崩溃，可能保留该临时文件。当前调用的普通异常会尝试核对创建时 file ID 后回收，但重启不会根据名字认领或删除。这一极端边界明确保留给后续恢复协议；本批次不声称任意崩溃都绝对零文件残留。

## 终结 checkpoint

ledger 的 nullable terminal 存储完整 canonical `Verified`、`Uninstall` journal 与 ContentHash。它保留 TransactionId nonce、TargetSid、package version/hash、phase/generation；不重建或改写身份。

1. `ClearVerifiedAsync` 在删除 active journal 前，验证当前 authoritative journal 与命令完全一致，将同一 Verified snapshot 写入 terminal 并重新读回核对。
2. Active journal 清除后，helper 仍须完成原有 null reload 和 SessionGuard.Complete。helper 内本次 clear 的后续 reload 不用 terminal 冒充仍未清除的 active journal。
3. Parent 的每次只读 Load 在 active journal 缺失时读取 terminal；有有效 terminal 就返回该 Verified snapshot，使 UI 保持准确 Uninstall recovery。若同时存在 active journal 和 terminal，必须完全一致；不接受更换目标/事务/包的请求。
4. 新 helper 对 terminal-only recovery 可重验 Verified state，不能凭缺失 active journal判成功；保存同一 Verified snapshot是可验证的幂等操作，其他相位写入拒绝。
5. 成功 Clear Uninstall 的公共 before-reply 回调先释放 operations（包括证书 persistence guard）和 stores，再执行清理；exclusive authority 和 application lease 一直保留。回调异常不得写成功 frame，不进入已 disposed stores 的 reconcile。
6. 固定目录逐个核对持久对象身份、当前安全描述符、无 reparse 的完整路径链和为空条件，再通过同一对象 handle 非递归删除并重新观察。陌生对象/缺证据保留；非空目录保留；未知 I/O/权限/身份检查失败为失败而不是 clean。
7. 六角色完整结果只允许 Missing、Deleted、RetainedUnprovenOwnership、RetainedNonEmpty。有保留项必须随成功 clear receipt 显式传给 parent，不能声称全部清净。清理结束后最后删除 ledger 并确认缺失，之后才能发送成功。

## 中断边界

| 断点 | 下次可用的事实 | 允许行为 |
| --- | --- | --- |
| 目录创建后、ID 提交前 | 没有该目录的持久 ID | 保留，不补认领 |
| terminal 写入前失败 | active Verified journal 仍存在 | 现有 Uninstall recovery |
| terminal 已提交、active journal 未删 | 两份完全相同的 Verified snapshot | 幂等重验和 clear |
| active journal 已删、目录清理中失败/进程退出 | ledger terminal 仍存在 | parent 暴露同一 Uninstall recovery；新 helper 逐项重验 |
| 部分目录已删 | 终结 snapshot 和剩余角色 ID 仍在 | Missing 幂等通过，其他对象仍需身份/安全/为空核验 |
| ledger 最后删除后、成功回复丢失 | 没有 pending checkpoint；parent 未收到成功 | 保持不确定；不能仅凭 journal/ledger absence 改判成功 |
| 对同一 Clear 的认证重放 | 原 nonce/state + 新 helper 真实最终验证 | 原 SessionGuard 的 VerifyCommittedReplay；仍执行 before-reply 清理 |

最后的回复丢失窗口不新增永久成功历史。若重试，必须先证明旧 helper 退出、保留原 command/state，重新建立正常认证边界并真实验证包、服务、机器文件、证书与目录；不能复用 faulted broker 或让一般只读 package inspection 代替这些事实。当前结果保持 uncertain 是允许且准确的结果。

## 改动边界与验证

Windows 新增固定 layout/ledger codec、保护文件 persistence、创建记录、事务 store decorator 与终结清理 native；接入 ProtectedStateStores、DirectoryNative、短期 parent reader 和 Windows helper authority。Core 新增六角色报告、clear receipt、严格 helper response 与 before-reply 回调，由独立协作者实施。

测试覆盖：创建/记录顺序、既有目录不认领、对象替换/ACL/reparse、foreign 同名 ledger、异常/取消、terminal-only恢复、清除前后持久边界、释放资源但保留两把 lease、六角色固定顺序、非空与未知归属保留、删除后重观察失败、Clear committed replay、成功回复前异常。所有本地测试使用隔离临时普通路径和注入 native，不修改本机产品/代理/证书。
