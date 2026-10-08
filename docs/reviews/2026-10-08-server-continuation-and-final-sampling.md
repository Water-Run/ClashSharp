# 服务器接续与结束采样测试修正

本轮遵循用户更新的环境要求，停止本机 Sandbox 验收，后续产品原生测试使用已有服务器。服务器维护期间完成了 CI 故障定位和测试修正，并核对了三份 GeoData 的上游二进制来源。

后续结果：准确 `df93bf0` 的两项 CI 及原始 6,631 项测试全部通过，证据摘要逐项匹配。GeoSite 已取得完整内容重建和准确字节复现材料；服务器恢复后完成 158 文件备份，等待原测试账户的交互登录。见[最新接续](2026-10-08-geosite-reconstruction-and-server-resume.md)。

## CI 失败与修正

提交 `32b9531e85522f91c7a4f6ff50591cbec81936ff` 已推送主线。[CI 37714850445](https://github.com/Water-Run/ClashSharp/actions/runs/37714850445) 的离线打包成功，主程序为 **4,027 通过、1 失败、0 跳过**，后续三个 Installer 集因前置失败未执行。

失败用例是 `BoundFinalSample_UsesOwnedDiagnosticsAndPreservesCallerCancellation(outcome: "timeout")`。原始 TRX 记录持续 6.343 秒，异常来自测试第 94 行的五秒 `WaitAsync`。该用例等待生产代码的两秒真实计时器取消；繁忙环境中的计时器和异步继续执行会影响外层等待。原失败报告和日志保留，不能用此前本机通过或简单重跑替代这次失败。

`BindDataScope` 现在接受可选 `TimeProvider`，默认使用系统时钟。结束采样仍共享一个两秒期限，刷新和采样步骤都接收同一个关联取消令牌。测试使用手动计时器触发真实取消路径，明确检查两秒期限、一次计时器、阶段调用次数及资源释放。共享网络操作顺序的测试也由手动时钟持有采样阶段，直到显式放行。

增加采样阶段超时、采样阶段调用方取消和正常完成三个场景。失败和超时写入当前数据代的诊断后继续停用；调用方取消保持取消结果并保留核心；成功完成没有失败诊断。测试在断言失败时也取消并等待自己的操作，随后才释放日志数据库。

本地相关集 **33 项通过**，最终主程序全量 **4,031 项通过、0 失败、0 跳过**，用时 4 分 6 秒。18 项目 Release/x64 构建零警告、零错误，完整格式检查通过。修正候选的 CI 另行绑定验证。

## 服务器接续准备

本轮本机沙箱 `19062119-8a54-4511-b28d-fb88830fb113` 已按准确身份关闭，CLI 再次确认不存在。输入文件和宿主代理指纹保持；用户停止 Computer Use 时的安装点击没有完成证据，本次不计安装成功。

维护前只读检查确认，指定 Server 2025 仍安装 `8289dec` 对应的 `1.0.0.0`。测试账户及其 153 个 LocalState 文件存在，服务运行，应用与内核退出。但原 `C:\ClashSharpValidation` 目录已不存在，旧构建任务指向的脚本也已不可用。

随后 SSH 连接在握手阶段被重置，用户确认服务器正在维护。本轮目录准备命令未取得成功回执，恢复连接后先检查实际目录和权限，避免直接重跑或认定已完成。账户签名能力探针和关闭状态数据备份脚本已在本地准备并通过语法检查，尚未在服务器执行；现有产品和用户数据尚未进行替换。

`32b9531` 源码归档与 CI 开发包已取得。九个发行文件、三个载荷项及两份材料归档的摘要已核对。由于该提交的测试失败，而且 Installer 为开发版未签名输出，保留为故障和打包证据，不能直接作为通过验收的候选。

## GeoData 来源核对

固定版本的生成流程直接复制 `Country.mmdb`、`GeoIP.dat` 和 ASN 数据，另行生成 `GeoSite.dat`。本轮从相应上游 Git Blob API 读取原始内容，逐字节比较现有发布输入，同时重新计算 Git Blob SHA-1 和发布清单的 SHA-256。

| 发布文件 | 上游项目与文件 | 匹配的不可变 Git 对象 |
| --- | --- | --- |
| `Country.mmdb` | `Loyalsoldier/geoip` 的 `Country.mmdb` | [884d0266ce764142c089f6bc96725b0a5e418449](https://api.github.com/repos/Loyalsoldier/geoip/git/blobs/884d0266ce764142c089f6bc96725b0a5e418449) |
| `GeoIP.dat` | `Loyalsoldier/geoip` 的 `geoip.dat` | [c9581b65e2b6210f54c8d20c248d49c3e86b355f](https://api.github.com/repos/Loyalsoldier/geoip/git/blobs/c9581b65e2b6210f54c8d20c248d49c3e86b355f) |
| `ASN.mmdb` | `xishang0128/geoip` 的 `GeoLite2-ASN.mmdb` | [de3d9bd8bd98c877d330f4d9437fe466a88ec5b2](https://api.github.com/repos/xishang0128/geoip/git/blobs/de3d9bd8bd98c877d330f4d9437fe466a88ec5b2) |

三份文件的长度、SHA-256 和实际字节全部匹配。这证明对应二进制在上游项目中的来源；它们的生成输入、相关条款以及 GeoSite 的多项动态输入仍需进一步记录。查询未取得 2026-09-05 至 09-07 的生成运行记录，当前 `geoDataUpstreamInputRevisionsRecorded` 继续为 `false`。

本轮源码、开发包、失败 TRX、最终本地回归、格式日志、服务器准备脚本和 GeoData 字节核对均位于被 Git 忽略的 `artifacts/verification/server-continuation-20261008/`。完整产品目标保持进行中；服务器恢复后接续原生验收，Windows 11 的剩余范围使用远程环境完成。
