# 正式发布保护的只读预检

当前手验候选仍为 `1813a93`／`8aacb768`。本次只增加发行准备工具和 CI 契约检查，不改变已安装程序、用户数据、签名材料或服务器代理状态。

`Tools/Get-ReleaseEnvironment.ps1` 通过现有 GitHub CLI 身份读取固定仓库的环境清单和 main 分支。它要求已有的 `release` 环境、受保护的 main、仅允许受保护分支的部署策略和人工／团队审批配置；连续两次规范化观察不同也会拒绝。缺失或不完整时仍保存失败回执。回执只证明所读配置，不能证明实际部署审批、管理员绕过策略、正式签名或产品手验通过，所有回执的 `publicationAuthorized` 均为 false。

新增 `Release protection preflight` 工作流仅允许手动从 main 运行，权限为只读，不声明 `environment`，不创建环境、不配置密钥、不签名、不发布。实际成功或失败观察均尝试作为产物保存。正式发布流程仍需另行接入实际审批和正式签名，不得用此预检代替。

原服务器独立源码副本的五个变更文件逐项 SHA-256 匹配。Windows PowerShell 5.1 和 PowerShell 7 分别通过发行配置契约37项（含24个拒绝用例）、CI范围契约80项以及全部62份受跟踪PowerShell源码的语法检查。测试入口使用与 CI 相同的脚本调用方式。另保留一次旧范围脚本通过 `-File` 调用时默认参数未解析到脚本目录的失败记录，不把该调用当成通过。

现有 GitHub 身份的真实只读负向核验得到 `release.environment.missing`，失败回执已写入；运行前后 environment 数量均为0，没有隐式创建。证据保存在 `artifacts/verification/server-release-preflight-20261027`，服务器副本位于 `C:\ClashSharpValidation\release-preflight-20261027`。推送后的完整 CI 和工作流实跑结果另行记录，不用离线用例冒充云端配置通过。

当前正式发行仍缺正式签名配置和受保护发布环境；MSIX 发布者为 `CN=linzh`，签名主体和包身份变更需要明确处理。保留用户逐项手验、反馈修正与确认后再发布的顺序。开始菜单入口继续完全由 MSIX 管理，没有增加安装选项。
