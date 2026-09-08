# 安装入口配置与真实事务验证

> 后续 M5d 故障测试发现服务装配失败：原运行记录只有瞬时 SCM 状态，不能证明服务
> 持续可用。现已加入装配修复、就绪握手和私有运行数据清理，详见
> [后续缺陷与修复说明](2026-09-08-service-readiness-and-runtime-cleanup.md)。

此前迁移期的默认关闭开关也用于正式打包：即使最终 EXE 已签名，WPF 启动仍选择
预览运行时，helper 入口也返回拒绝。完成首轮生产事务路径的客体观察后，正式打包现在
明确编译生产运行时；开发包明确关闭它，普通项目构建继续默认为预览模式。

## 打包与运行边界

`Get-ClashSharpInstallerMutationRuntimeProperty` 只返回一个明确的 MSBuild 属性，
不访问证书存储、不执行程序。`build.ps1` 将它用于既有 WPF publish 命令：

| 配置 | 生产运行时 | 既有输出条件 |
|---|---|---|
| 普通项目构建 | 关闭 | 可检查布局的预览入口 |
| `build.ps1 -Development` | 关闭 | 带 Development-Unsigned 文件名和标识 |
| `build.ps1` 正式签名路径 | 开启 | 嵌入清单、受控签名、固定签名者及可信时间戳全部通过 |

正式文件名仍只在签名与最终验证之后产生。源项目的 formal build 和嵌入清单检查
继续约束显式启用；运行时仍验证精确 EXE、签名者、载荷、账户与 helper 通信端点。

`Test-InstallerBuildProfiles.ps1` 调用真实 MSBuild 评估上述三个配置的最终属性与
编译常量，再直接运行验证目标，确认缺少正式标志、缺少清单或清单不存在时失败。
该检查不编译、不签名、不启动安装器，也不导入证书，适用于开发机和 CI。
[MSBuild 属性评估语义](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)
用于区分配置证据与已发布程序的运行证据。

## 生产引擎的客体结果

2026-09-08 的独立 Windows 11 x64 Sandbox 使用 `1d387a6` 源码、同一 CI MSIX
载荷及独立测试入口，直接组合 `WindowsInstallerParentEngine.CreateDefault` 与
`WindowsInstallerMachineHelper.RunAsync`。测试入口和 helper 使用同一个签名 EXE；
未替换生产服务、包、证书、文件、身份验证或事务适配器。

- 4 个 parent 进程分别检查、安装、修复、卸载，另有 3 个独立 helper 完成认证通信。
- 三个事务均返回 `installer.completed`，达到 Verified，且没有待恢复日志。
- 安装和修复后，精确 1.0.0.0 包已注册，ClashSharpMihomo 服务运行，机器与物理
  用户 TrustedPeople 证书存在，两个服务目录存在；修复保留所有权记录原字节。
- 产品卸载完成后的快照确认包、服务、上述证书和服务目录均不存在，产品状态文件
  集合为空。这份快照在测试兜底清理之前取得。
- 客体 9 项清理结果通过，独立 Sandbox 已销毁；只读输入未变，本轮主机代理未变。

运行 ID 为 `b6f5fae5baa44aab9f84d632a74cfe3e`，客体 ID 为
`ab2709bd-4b56-4f47-a3de-4be388e67ae1`。本地脱敏收据在
`artifacts/verification/installer-engine-m5c.json`，原始报告保留于对应忽略目录。
报告不得把服务命令行中的 IPC 凭据复制到文档或发布资产。

## 验证范围

测试使用仅在客体导入的一次性签名信任，无正式 CA 或时间戳发行证明。客体卷根
仍使用 M5b 已记录的精确 ACL 收紧夹具，最后恢复原 SDDL，生产目录策略没有放宽。
这些结果不能外推为任意账户、升级、断电或所有故障恢复组合已经通过。

WPF 候选在另一客体中完成签名和启动，但 computer-use 原生管道不可用，未观察
页面。首轮快照还遇到客体 CIM 被拒绝；监督脚本已销毁该客体，后续测试改用服务
控制器和注册表读取。WPF 点击、视觉与完整交互验收仍待执行，不能用引擎结果替代。

当前发布验收仍未完成。上述配置连接让正式构建具备产品功能，不等同于允许把
未签名开发包或一次性测试包发布为 1.0.0 正式版。
