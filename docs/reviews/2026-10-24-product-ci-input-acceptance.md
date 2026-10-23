# 产品CI输入执行审计的实际接受与拒绝

`905d69ba2918f2cb639d4f54baeea12cf7d6905a` 的 [完整CI run40019974052](https://github.com/Water-Run/ClashSharp/actions/runs/40019974052) 两项job成功。四份TRX下载并在服务器核验 **7,263／0／0**，报告归档SHA256 `5630F8E4EF2AD48F109746BC3CA864FEACDE80E956D08E8679D90CE43ADC2FE5`；三项下载artifact大小／GitHub digest一致，两份scope为准确源码的product=true／complete=true。

独立输入审计工作流也在准确905代码中完成真实CLI正反验证，而非仅使用离线夹具：

| 运行 | 输入及实际结果 |
|---|---|
| [40020027713](https://github.com/Water-Run/ClashSharp/actions/runs/40020027713) | 输入完整73c707b／run40016254087；成功，输出两项实际job和四种产物元数据。artifact id12530142182，ZIP SHA256 `8b96ba0413ca33c779621090652285eeb9af6845a4be7e7271f5b4b238aacf7c`，JSON SHA256 `31b0129ebf8bbc869cb4034b924bac12c27dc282a4b135064193348f43178221` |
| [40020033399](https://github.com/Water-Run/ClashSharp/actions/runs/40020033399) | 输入绿色文档e448408／run40019155897；按预期失败，准确诊断`product-ci.required-step:Set up pinned .NET SDK`，没有描述符或其他artifact |

正例中的artifactBytesVerified、rawTestReportsVerified及promotionAuthorized均为false。它只证明该阶段实际产品步骤与产物元数据；不能把该描述符当作签名候选、字节验证或发布审批。负例失败是门禁正确拒绝，不是将文档轻量运行改为失败；原文档运行的两个必需job仍绿色。

服务器双版本各76项输入／权限契约和各70项组合范围契约、实际架构16／0／0通过。真实API快照与CLI观察都保留，接续目录分别为本地 `artifacts/verification/server-product-ci-gate-20261024` 和 `server-ci-scope-20261024/ci-40020027713`。

当前源码继续新增[产物字节与报告验证](../design/2026-10-24-product-ci-artifacts.md)；该下一层已完成服务器26项契约和真实四归档核对，但新CLI／工作流的下载分支及准确完整CI仍待验证。原安装为ebbbb65，原47数据和服务保持，没有正式Release、签名环境或宿主变更。
