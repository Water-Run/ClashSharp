# 产品CI产物字节与原始报告验证

这是[执行元数据审计](2026-10-24-product-ci-input.md)之后的一层。`ProductCiArtifacts.psm1`消费由可信调用者通过新鲜API取得的描述符，`Get-ProductCiArtifacts.ps1`则直接取得该描述符、下载准确artifact id、验证内容，再次核对运行元数据没有变化，最后才创建新的证据文件。

## 下载与身份

四类输入分别是两项scope、dotnet-test-results和当前开发安装包。每个输入按artifact-ID.zip保存，与API的长度及SHA256逐一比较。已有完整匹配文件可复用；部分、改变或重解析文件保留并拒绝，不覆盖重试。输入目录及祖先拒绝重解析点。

CLI要求PowerShell7.4及以上，因为[原生命令stdout重定向从7.4起保留字节流](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_redirection?view=powershell-7.4)，ZIP不能经过字符串编码转换或与stderr合并。请求固定github.com端点，gh凭据不输出。下载结束重新取得同一运行的执行元数据，若重跑、状态、job或artifact身份变化，不生成成功结果。

## 校验即消费的边界

验证器对每个归档保留拒绝写入／删除共享的读取句柄，用同一字节流计算摘要、读取ZIP及消费所选成员。拒绝活动写者。ZIP库存限制数量／展开预算，拒绝根路径、驱动器路径、反斜线、`.`／`..`、尾点和大小写碰撞；只向独立GUID目录写入四份固定名称TRX，按实际解压字节数限制长度。

两份scope必须明确为product=true并绑定准确head，JSON字段不能缺失、重复或夹带未知键。保守回退的complete=false仍可有效：它要求完整产品检查，而不是轻量跳过。随后验证四个固定报告角色及各自独立run身份，复用现有TestResultEvidence核对计数、定义、执行记录、通过状态和原始字节摘要；不导出日志正文或临时报告路径。报告覆盖范围仍以受信任工作流的无过滤命令及实际步骤为依据，不由单个TRX自证。

开发包只检查已认证ZIP的九项普通库存，包括DEVELOPMENT-UNSIGNED标记，不解压或执行程序，不验证嵌套MSIX、机器载荷或签名。证据因此分别记录：

- artifactBytesVerified、rawTestReportsVerified为true。
- developmentPackageInventoryVerified为true。
- developmentPackageSignaturesOrNestedPayloadsVerified、promotionAuthorized保持false。

结果是对应digest的快照，不是返回后对可写目录的永久租约；后续若再次消费文件，仍须保留句柄或按准确摘要重验。受保护签名候选、候选真实验收及同digest提升仍是独立门禁。

## 工作流与验证

`product-ci-input.yml`保留元数据快速模式，新增verify_artifacts布尔输入。选择true才下载并验证全部四类输入；候选流程消费字节证据时必须要求这个分支成功，不能退回只有元数据的绿色结果。失败时不会上传成功字节证据。

```powershell
gh workflow run product-ci-input.yml --ref main `
  -f run_id=40016254087 `
  -f source_commit=73c707bcf606de83bee733a169690f51f1f0fbe0 `
  -F verify_artifacts=true
```

服务器离线契约26项通过，覆盖摘要／长度、活动写者、scope伪造／重复字段、路径穿越、大小写碰撞、缺失／失败／重复TRX、私钥或未知开发文件、junction及保守全量回退。组合范围契约71、执行元数据76、实际RepositoryTopology16／0／0通过；仅在服务器执行，无本机产品或Sandbox。

真实输入使用run40016254087的四项API身份和归档，最大开发归档415,507,750字节；全部字节、两份scope、四份原始报告及开发库存通过，原始报告 **7,263／0／0**。未执行归档程序，也未修改原安装／数据。原始记录保留在服务器 `C:\ClashSharpValidation\ci-artifacts-20261024`；实际CLI下载及工作流分支尚需上线后端到端复验，不能把服务器模块验证冒称已完成该分支。
