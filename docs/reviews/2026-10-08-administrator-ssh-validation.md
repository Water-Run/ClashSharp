# Administrator SSH 接续与服务器回归

用户确认原来的 `ssh -p 26222 Administrator@192.168.10.104` 入口可用后，已从该入口恢复服务器构建和验证。此前把缺少 `CshAT260922` 交互桌面当成整体阻塞，范围过宽；该条件只限制原测试账户的界面验收。后续继续使用服务器，本机没有启动 Sandbox。

## 已完成的服务器验证

主机为 `WIN-07OBU2PV8C6`，Windows Server 2025 Desktop、PowerShell 7.6.6。准确源码 `df93bf0633ed5b4611daa7d0b802c447f05dd918` 的归档摘要、微软 SDK 10.0.201 的官方 SHA-512、四份 GeoData 和四份 mihomo 构建输入均已核验。独立目录中的锁定还原及 18 项目 Release/x64 构建成功，零警告、零错误。

| 服务器实际执行范围 | 通过数 |
| --- | ---: |
| 主程序 | 4,031 |
| Installer Core | 1,093 |
| Installer Presentation | 184 |
| Installer Windows | 1,317 |
| 合计 | **6,625** |

四份报告均无失败、无跳过。六项 `WindowsCurrentUserCertificateStoreAdapterTests` 明确要求一次性托管 CI 或 Windows Sandbox，本次过滤掉，未计入服务器通过数；它们由准确候选已有的完整 CI 覆盖。七项原生目录清理测试在本服务器实际运行，操作自建目录、真实句柄、ACL、目录替换与数据流；结束后没有遗留测试目录。

服务器先运行证据门禁，再取回四份原始 TRX 独立验证，运行身份、实际结果、数量及 SHA-256 全部匹配。另行核对两项管道用例、七项原生目录用例及六项结束采样用例均实际执行通过。现有完整 CI 仍为 6,631 项通过，两个范围分别记录。

## SSH 与本地管道的登录上下文

第一轮直接在 SSH 进程中执行主程序测试，得到 4,029 项通过、两项 `PipeServer_CompletesFramedHelloForAllowedUser` 失败，错误为 `UnauthorizedAccessException`。该登录令牌含 Network SID `S-1-5-2`；生产管道 ACL 明确拒绝此组。

随后通过同一 SSH 管理入口，在服务器已有的 Administrator 会话 2 中启动无触发器的一次性计划任务。实际收据确认会话 2、不含 Network 登录组；未修改账户凭据、管道权限或测试断言。相同源码的四组回归全部通过。直接 SSH 的失败日志和 TRX 单独保留，没有覆盖或计为成功。

这区分了服务器管理通道与本地 IPC 执行上下文。需要本地登录令牌的检查可由已存在的本地会话执行，不能把 SSH 的连接成功等同于所有本地管道都允许该令牌，也不应据此停止其他开发工作。

## 内部候选构建

CI 的 `Development-Unsigned` 安装器编译时关闭了生产安装和提权入口，重命名或补签不能启用。之前保留的补签失败副本没有作为可安装候选使用。

本轮从准确源码调用仓库原有的 `Installer/build.ps1` 生产构建路径，使用服务器新建、限定七天的内部验收证书。完整打包成功，最终八个文件重新核对长度和 SHA-256；第三方说明与 mihomo 源码材料仍与 CI 中的字节一致。

| 最终内部产物 | SHA-256 |
| --- | --- |
| `ClashSharp-Installer.exe` | `E01619CC6C7D27DBFA747FF8C04CD1C383700F1ED1B5BA406F2A5E3FDC16C6D8` |
| `ClashSharp_1.0.0.0_x64.msix` | `75821080015201F65589328980FA0BF50B62C2FB727ED398B53F22593F357613` |

安装器使用内部证书 `ADCFBBBAF5AD225F9ACEB7141D6D070EEAC23EE6`，MSIX 使用 `9F2E4129CAFE25323BB5CDD93D00DD6C8E865A76`。构建期间的临时信任下，Windows Authenticode 为 `Valid`，固定 Windows SDK 的签名及 RFC3161 时间戳验证通过。正式发行签名继续按用户既有安排后续配置。

两张临时根信任与加密 PFX 副本均已清理并独立确认不存在；七天验收证书保留在服务器 Administrator 的 My 存储供接续使用，旧证书未修改。该产物在明确的内部信任条件下通过验证，不具有公共发行信任。

清理信任后，从 Administrator SSH 再次执行最终安装器的 `--verify-payload` 只读入口，实际载荷审计通过：四个载荷文件、七个机器组件、版本 `1.0.0.0`，主 MSIX 摘要一致。随后八个产物均可独占打开，没有遗留审计句柄。一次性任务以退出码 0 完成，按准确名称与动作核对后撤销，独立确认不存在。

## 安装与数据状态

服务器回归结束及最终签名候选核对后，原 `8289dec` 安装包主程序集摘要保持为 `BF835EF0C6D03D04D4B175065C9A37C071646A581008573D51454925CA1A873C`。158 个原数据文件和 158 个备份副本分别逐文件核对通过；服务仍为 Running、PID 12152，应用、内核、看门狗退出，公共安装事务不存在。原账户完整代理指纹仍为 `C4CB7760F87D6A3AD3C80022F6747BBD78F8383916AA2476C087206C1ABEDC64`，代理关闭。

本轮构建与组件回归不能替代当前候选的安装、修复、卸载或 GUI 验收。后续先完成准确内部候选的维护与故障验收，继续数据替换/退出恢复组合，再补齐远程 Windows 11、页面和显示矩阵。GeoData 历史来源与相关发行材料缺口继续保留。

原始证据位于 `artifacts/verification/server-continuation-20261008/administrator-server-evidence/`；服务器工作目录为 `C:\ClashSharpValidation\continuation-20261008\administrator-df93bf0`。两个签名旧引用不可用和缺少原测试账户桌面的事实仅保留其各自范围，不再作为整个开发任务的阻塞条件。
