# 候选准备前的产品CI输入审计

`ci.yml`保留两个必需job，但纯审查文档运行可以不构建产品。因此“两个job都是绿色”不能独立作为候选输入，旧脚本零散读取结论的方式也不宜直接搬到发行流程。

新增`eng/ProductCiRun.psm1`和`Tools/Get-ProductCiRun.ps1`，从固定github.com仓库读取准确run、完整jobs／artifacts列表及当前ci.yml身份。模块逐项要求：

- 准确run id、源码SHA、主仓库与head仓库一致，事件为主线push；工作流路径／id一致且仍启用。
- run和两项唯一job均成功完成，job归属同一个run与源码；必要的构建、格式、依赖、运行组件、四项测试、原始报告、覆盖率与打包步骤分别实际成功，不能是skipped，也不能有重复同名步骤掩盖结果。
- API总数与收到的列表一致，拒绝不完整分页、重复job／artifact id。四种当前产物角色唯一、未过期、长度非零、SHA256格式正确且归属准确run／源码。

CLI通过现有gh认证读取固定hostname和固定仓库端点，不导出令牌；完成后再次读取run，拒绝观察期间发生重跑或状态变化。输出只使用新文件，不覆盖已有证据。

## 结果的权限边界

描述符kind为`product-ci-execution-metadata`，记录已执行的job和待消费产物id／digest。以下字段始终为false：`artifactBytesVerified`、`rawTestReportsVerified`、`promotionAuthorized`。模块本身不能认证调用者提供的JSON；CLI取得的新鲜API观察也不代替实际下载、原始TRX验证、受保护源码／签名策略、候选的真实验收或发布审批。

它是候选输入链的前置检查，不是候选签名器、字节证明或发布授权。当前开发安装包仍为开发用途，不能因为该输入审计成功就公开提升。

## 独立只读工作流

`product-ci-input.yml`仅支持main上的手工调度，权限只有contents:read和actions:read；输入通过环境变量传递，避免插入shell源码。工作流调用实际CLI，仅在成功时上传描述符，不配置环境／签名秘密，不导出私钥、不部署、不创建tag或Release。

```powershell
gh workflow run product-ci-input.yml --ref main `
  -f run_id=40016254087 `
  -f source_commit=73c707bcf606de83bee733a169690f51f1f0fbe0
```

该示例仅审计指定历史产品运行的元数据。主线CI会在产品路径中对门禁模块运行双版本离线契约；独立审计工作流还需用真实完整与真实轻量运行做正反验收。

## 当前验证

服务器双PowerShell版本各76项元数据／权限／工作流契约通过，既有范围契约因新增两个步骤各70项通过，实际RepositoryTopologyTests为16／0／0。覆盖绿色但跳过产品步骤、fork／PR／错误分支、错误SHA／工作流、缺失／重复／截断job和artifact、过期／错误digest及返回策略的独立性。没有执行产品或本机测试。

真实API快照正例为run40016254087／源码73c707b，模块接受其产品执行元数据，仍保留三个false边界字段；负例为绿色文档run40019155897／e448408，准确拒绝已跳过的SDK步骤，没有将其作为产品输入。快照经摘要核对后在服务器验证，不把离线快照验证冒称CLI实时端到端验收。

服务器接续目录为`C:\ClashSharpValidation\product-ci-gate-20261024`，最新离线结果为`result-r3.json`，真实观察位于`real-full`和`real-review`。后续仍需下载并验证产物字节和四份原始报告，再接受控签名候选、真实平台矩阵和同digest提升。
