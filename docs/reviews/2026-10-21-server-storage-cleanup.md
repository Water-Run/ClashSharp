# 服务器构建缓存清理

用户指出远程服务器存储不足后，先只读盘点 `C:\ClashSharpValidation`，再分两批删除已结束节点的可重建缓存。当前 `hero-layout-dialog-20261021` 节点完全排除；未在本机运行产品或 Sandbox。

| 批次 | 缓存目录 | 文件 | 逻辑大小 | 实际释放 | 清理后 C 盘可用 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 最近十二个已结束节点 | 709 | 60,708 | 29.25 GiB | 22.05 GiB | 41.10 GiB |
| 更早且有源码身份记录的节点 | 1,618 | 140,563 | 74.60 GiB | 53.65 GiB | 95.16 GiB |

两批共移除 **2,327 个缓存目录／201,271 个文件**，实际释放约 **75.70 GiB**。C 盘总容量 475.81 GiB，最终可用约 95.16 GiB。逻辑大小与实际释放不同，不以逻辑大小冒充回收空间。

删除范围仅为明确列出的旧节点中 `source`、`candidate/source`、`sdk-native-source` 下各项目的 `bin`／`obj`，以及 `artifacts/installer/packaging-staging`。每个目标先核对解析后的绝对路径、严格目录模式、非当前节点、无重解析点及无活动进程／服务引用，再以原生 PowerShell `Remove-Item -LiteralPath` 删除。保留逐目录清单和结果，不跨 shell 拼接删除命令。

源码快照、签名 release 安装包、数据备份、验收证据、SDK 运行时、共享 NuGet 缓存和签名材料均保留。两批各自重新核验最近关闭数据的 46 个文件和恢复安装器，共 **47 个受保护文件**，前后 SHA-256 一致。

服务器可接续记录位于 `C:\ClashSharpValidation\cleanup-20261021-{plan-v2,removed,result}.json` 和 `cleanup-20261021-older-{plan,removed,result}.json`；清单和结果已下载到本机忽略目录 `artifacts/verification/server-cleanup-20261021`。初始 `plan.json` 的 PowerShell 字典投影计数有误，保留原件，实际首批操作使用修正后的 `plan-v2.json`，未据错误统计扩展删除范围。
