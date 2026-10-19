# 构建输入 BOM（2026-10-19）

第三方通知包新增确定性的 `bom.cdx.json`，使用 CycloneDX 1.6 标准表示已核验的 NuGet 构建/运行输入、固定 mihomo 二进制和四项 GeoData。当前真实构建输入为 75 个 NuGet 包、108 份原始文档和 80 个 BOM 组件。

## 身份、范围与绑定

组件记录包 URL、精确版本及真实归档/二进制的 SHA-256。NuGet 对签名包使用的内容摘要另列属性，避免将它误当成实际归档的 SHA-512。许可表达式与其他声明保持原义，GeoData 不推断聚合许可，也不把其移动上游来源标为完整。

这是保守的构建输入清单，不声明每个 NuGet 文件均已部署，也不虚构未核实的依赖边；完整性字段明确为 `incomplete`。[CycloneDX 标准](https://github.com/CycloneDX/specification/blob/1.6/schema/bom-1.6.schema.json)及两份引用 Schema 以原始字节固定在源码中，附原始 Apache-2.0 许可。测试仅在内存中绑定引用，离线检查完整 Schema，无需获取移动的外部定义。

生成器将清单放入已有 `THIRD-PARTY-NOTICES.zip`；验证器由同一库存重建清单并逐字节比较，拒绝缺失或改变的 BOM。该 ZIP 随 MSIX 与安装载荷绑定，`payload-provenance.json` 另记录 BOM 摘要和组件数；没有增加未绑定的外置扫描报告。

## 服务器验证

- 干净 Git 归档的说明包测试在真实签名包检查处发现缺少 restore 产物；按锁文件还原后继续，原失败日志保留。
- 修正生成器返回元数据，将 BOM 从“原始文档”计数中排除，并返回摘要和组件数。新增断言要求生成器与验证器的这些字段一致。
- 最终说明包契约 **39 场景、70 断言通过**，包括缺失/修改 BOM、组件摘要篡改、官方 Schema 拒绝无效组件类型及真实签名 NuGet 内容完整性。
- 运行时打包契约通过：2 个有效包、30 个拒绝包；源码材料契约 24 场景、46 断言通过。
- 27 个规范 PowerShell 输入在 Windows PowerShell 5.1 与 PowerShell 7.6.7 下解析通过。Git 文件发现入口不适用于无 `.git` 的归档目录，该次发现失败日志保留，随后按规范输入清单解析相同受控源文件。
- 真实依赖生成的 BOM 通过完整离线 CycloneDX Schema，80 个组件，BOM SHA-256 为 `ca52a5b25adaece5839683a225990e2ee1c0c59ca70c6190d7f9471ffbc5a370`。

## 最终容器与准确 CI

实现 `a863d0f` 的[完整 CI](https://github.com/Water-Run/ClashSharp/actions/runs/39266188145)两项作业成功，四份原始 TRX 在服务器核对 **7,066 项全部通过、零失败/跳过**。构建输入及三个标准 Schema 绑定 Git 归档；实际说明包在 `en-US`、`zh-CN`、`tr-TR`、`sv-SE` 和 `fa-IR` 下均重建相同 BOM 摘要。

内部签名候选的主程序集为 `DCB2CA0ECB75975CEC69DB9D7826475D8D5177BE044D1A43073C103ABFCB2D5A`，MSIX 为 `4F4AFD1F21CF3726B0111855C76DF1BFA1AB5FFD14A19A7217470F9C6E798935`，安装器为 `66C1D27EE8D62166AAC4AF6D695CDECC8FF4C22B63914CC86B9D2885FBD23190`。签名、时间戳与载荷审计通过；最终 MSIX 内通知副本、外置同份 ZIP 及 `payload-provenance.json` 的清单摘要/80 组件记录逐项一致。最终包内 BOM 再次通过完整离线 Schema。

临时 Root/PFX 已清理，证书多重集保持；原 45 文件及备份、原安装和服务 PID 7140、原代理保持，构建任务归档移除。43 项证据归档下载后逐项核对，SHA-256 为 `F32F0F2A39EF316FD82EE8CC6BDAC6703CBC638F37ACE5B12334A41E4E18263D`，接续点 `build-input-bom-20261019/checkpoint.json`。

本机没有运行产品构建或测试。此次修改是构建输出与验收合同，已验证最终容器，未重复安装或执行未变的界面流程；服务器安装仍为 `1df1fe4`。新候选仅用于内部验收，没有发布正式版本。全局 TUN、DNS 和 Windows 11 平台验收仍单独保留，不由 BOM 或构建成功代替。
