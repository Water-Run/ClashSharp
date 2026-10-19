# 真实 TUN 验收前置边界（2026-10-19）

应用/服务/Agent 的全局 TUN 验收继续推进。本轮完成原数据保护、官方 SDK 客户端的原数据拒绝测试，以及远程管理路由的创建/撤销预检；**没有启动全局 TUN，没有卸载当前应用，也没有将这些前置结果计为产品 TUN 通过**。

## 原数据与客户端边界

服务器重新备份 45 个关闭状态文件，逐项确认与前次验收后的备份一致。客户端使用官方 MCP SDK 2.2.0，准备阶段要求新的隔离数据代证明：原数据已移开、45 文件备份有效、当前 generation 描述文件的路径和摘要完全匹配；每次工具调用前重新核对。

辅助客户端在服务器编译，零警告/错误。针对现有原数据运行拒绝用例时，得到 `disposable_generation_proof_missing`，确认在读取剪贴板或连接 SDK 之前停止。没有读取原账户 MCP 凭据，没有创建原配置或改变代理模式。实际隔离安装及 SDK 工具准备流程尚未执行。

## 远程管理路由边界

读取服务器现有 SSH/RDP 管理连接并为 4 个 IPv4 管理地址记录 Windows 当时选中的接口和下一跳，地址只保存在受限目录。根据 [Find-NetRoute](https://learn.microsoft.com/powershell/module/nettcpip/find-netroute) 的实际选择创建临时主机路由；[New-NetRoute](https://learn.microsoft.com/powershell/module/nettcpip/new-netroute) 明确指定 `ActiveStore`，不写持久路由，也不覆盖现有主机路由。

创建后逐个确认选中的 `/32` 路由仍使用原接口与下一跳。最后按精确目的、接口、下一跳、metric 和 protocol 核对后，仅删除本次新增的 4 条路由；所有原路由保持，最终路由表与基线完全一致。该过程未调用应用模式、服务、DNS 或 TUN 操作。

当前产品 TUN 使用全局自动路由，下一阶段必须先完成隔离数据集的独立限时清理与真实 SDK 激活/转发用例，再放行网络变更。现有辅助客户端只实现受保护的准备阶段，不能据此声称这些后续步骤已经实现或通过。

## 收尾与证据

原安装 `1df1fe4`、服务 Auto/Running、PID 7140 和原 45 文件/备份保持。前置路由均已移除。16 项前置证据归档下载后逐项核对，SHA-256 为 `2EAA29D549494D675D09DCCED7E54242EB548AD1DEBF4AEE17D9F0424597692F`，不含管理地址或原数据正文；服务器接续点 `native-tun-20261019/preparation-checkpoint.json` 明确标记 `globalTunActivated: false`、`serviceTunAcceptanceCompleted: false`。

下一步仍是受控的真实应用与服务 TUN 转换、直接数据包与 DNS 验证、退出/失败恢复，以及原数据恢复。Windows 11 客体环境也仍待核实；服务器本身是 Server 2025，没有把它的结果算作 Win11 客户端证明。本机没有构建或运行产品测试。
