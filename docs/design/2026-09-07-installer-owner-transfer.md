# Installer 跨账户换绑边界

## M4h 已实现的边界

普通 parent 仍固定 `AllowReassociation=false`。审查发现底层 ownership policy 和 association store 曾单独使用该布尔值放行未知或外来归属：前者为不明确的修复返回新 token，后者允许覆盖旧 association。这不是完整换绑证明，可能丢失恢复旧服务所需的凭据。

现在普通 provisioning 只接受两种状态：

1. 已存在目标 SID 的严格 association：继续使用原 token；
2. association、服务和机器载荷残留均不存在：创建新 token。

其他状态均在服务停止或 association 写入前拒绝，不再因请求携带换绑意图而放行。底层 association store 独立拒绝覆盖其他 SID、同 SID 不同 token、损坏 JSON 或不安全文件对象；只能创建缺失文件，或观察已经完全相同的 association。原有文件替换失败后的后置条件核对不变。

这项变更收紧的是普通入口，不代表已经实现了用户确认、双 owner 迁移或恢复界面。请求和 v2 日志仍保留原字段以维持协议读取兼容性，该字段只记录意图。

## 持久迁移的接入要求

换绑必须是独立用例。后续接入不能再次让普通写入方法凭布尔值覆盖旧证据：

- 由经过认证的 helper 获取机器独占租约，检查并持有旧、新账户的 App 生命周期屏障。缺失、重定向或不明确的旧 profile 不能当作旧 App 已停止。
- 在修改前保存旧/新 SID、旧/新凭据、可信 profile 身份、同一候选摘要和普通安装后续事务的身份。包含新凭据的记录仅允许 SYSTEM/Administrators 访问，不能写入 parent 可读 v2 日志、日志文本或 IPC 结果。
- 先持久阻断 App 启动，再用旧 SID/token/profile 构造旧 SCM tuple，验证、停止并删除旧服务。存在陌生服务、损坏 association 或未知 ACL 时拒绝，不能自动修复成当前账户。
- 明确区分服务已删除、机器目录 ACL 已转移、association 已转移、证书所有权状态已保存、Installer 状态目录 ACL 已转移以及最终验证。每一阶段的重放只接受预期的旧状态或已完成的新状态。
- 普通 v2 Prepared 日志必须持续存在到新账户的安装事务接管，避免迁移与继续安装之间出现 App 可以启动的空隙。目标账户没有包时应接续 Install；已有包时接续 Repair，不能绕过 Repair 要求已有包的规则。
- 现有 certificate ownership store 只保存一个目标 SID 的账本。旧账本不能改写 TargetSid 或在有引用时删除；需要保留原证书归属证据，并定义旧账户包的保留/移除及后续独立卸载路径。目录 ACL 转移本身不能解决这项多账户状态问题。
- 确认界面应明确说明旧账户的服务接管将停止，以及旧包、配置和证书如何处理。只有这条独立确认路径可以申请迁移权限；普通 Repair、静默参数和恢复一个不匹配的候选不能隐式发起换绑。

这些要求尚未全部实现。当前生产 mutation 门继续关闭，跨账户请求仍拒绝；不能将普通入口加固视为换绑功能完成。

## 验证

M4h 增加按观察状态、服务残留和意图标记展开的拒绝用例，并保留同 owner 的凭据复用验证。Windows 适配器测试使用 fake SCM/file ports 或临时目录，核对旧字节、服务和载荷保持，以及停止/写入调用没有发生。

Release x64 完整构建 0 警告、0 错误；Installer Core 560 项、Windows 开发机安全子集 415 项通过，均 0 跳过。当前用户证书存储的 3 项修改测试仅在隔离 CI 执行。证据为 `1.0.0-m4h-core.trx`、`1.0.0-m4h-windows-safe.trx` 和 `artifacts/verification` 中的同节点日志。
