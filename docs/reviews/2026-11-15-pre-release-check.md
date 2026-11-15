# Clash# 发布前检查与试用 Release（2026-11-15）

## 当前结论

首轮安全修复提交 `97150e787e32b1b22e07c88456611e81246876b4` 的完整 CI 共 13,754 项测试全部通过，零失败、零跳过。用户随后批准了完整安装器的沙盒安装，安装、服务部署及启动验收通过。试装时指出的弹窗尺寸问题已继续修复，见[独立模态窗口记录](2026-11-15-dialog-screen-bounds.md)；首轮 ZIP 已被这次修复后的交付替代。没有在宿主系统安装 Clash#，没有发布 GitHub Release。

本轮沿用维护者已接受的长期自签名发行方案和[上一轮验收边界](2026-11-10-release-pass.md)。本轮更新了内置 Mihomo、对应源码材料、发布检查脚本及资源契约测试，并将共用 ContentDialog 改为独立的原生模态宿主，尺寸按显示器工作区和 DPI 计算。

## 发现与修复

1. **内置内核存在已知漏洞命中。** 对实际内置 v1.19.33 的二进制扫描报告 48 项；独立下载并核对 GitHub 资产摘要的上游 v1.19.34 仍报告 47 项。[govulncheck 的二进制符号扫描](https://pkg.go.dev/golang.org/x/vuln/cmd/govulncheck)不是逐项可利用性证明，不能把这些命中当作已排除的风险，因此没有跳过安全门禁。
2. **重编安全依赖版本。** 以 mihomo v1.19.34、上游提交 `0455d0d28c4526937380505153e65a843d8612e8` 为基础，使用 Go 1.27.4，更新 brotli、compress、dhcp、x/crypto、x/net、x/sys、x/text 及必需的传递依赖。完整 go.mod/go.sum 差异与两个错误聚合格式修正见 `ClashSharp/Installer/SourceMaterials/security-dependencies.patch`。两个 `fmt.Errorf` 调用改为把已有错误文本作为字面量参数，修复新版 Go vet 拒绝非恒定格式串的问题。没有改变代理和路由业务代码。
3. **对应源码材料完整交付。** 固定原始源码、上游 vendor、官方 Go 工具链、保留的公开 CA、更新后的完整 vendor 和修改文件；本地安全覆盖归档有独立长度与 SHA-256 固定值。构建、归档校验及离线脚本均要求这五类输入，发布源码材料 ZIP 共 10 个文件。归档中含依赖原始许可证。新增来源越界、覆盖归档损坏及补丁被改的拒绝检查。
4. **PowerShell 5.1 入口修正。** 两个契约脚本在 `powershell.exe -File` 下，参数默认值求值时 `$PSScriptRoot` 为空。改为进入脚本后初始化默认模块路径；5.1 和 7 均复验通过。
5. **资源测试改为检查实际清单契约。** 移除固定旧内核版本的断言，核对版本格式、精确的上游 URL、说明文件和实际程序的摘要及长度。相关 314 项资源与架构测试全部通过。

## 测试与构建证据

本地证据统一保存在被 Git 忽略的 `artifacts/release-check-20261115/`。

| 项目 | 结果 |
| --- | --- |
| 锁定还原 | SDK 10.0.403，`restore --locked-mode` 通过，无锁文件漂移 |
| Release x64 构建 | 全解决方案及修复后的测试重编均为 0 警告、0 错误 |
| 本机首轮主程序 | 7,204 通过，0 失败、0 跳过，包含覆盖率采集 |
| 安装器核心 | 4,291 通过，0 失败、0 跳过 |
| 安装器表示层 | 402 通过，0 失败、0 跳过 |
| 安装器 Windows 适配层 | 1,844 通过，9 项按设计跳过；这些证书或系统目录用例要求一次性或提权环境。跳过的 theory 不展开数据行，故本地总数与隔离 CI 略有不同 |
| 替换核心后的输入与架构回归 | 219 项通过；全量复测发现并修正旧版本号断言后，314 项资源与架构复验通过 |
| 格式 | 本地诊断级 `dotnet format --verify-no-changes` 及最终提交的 CI 格式检查均通过 |
| PowerShell | 5.1 和 7.6 解析 66 个跟踪源文件，0 语法错误；20 组 PowerShell 7 契约及适用的 5.1 契约通过，改动边界另作复验 |
| 第三方说明 | 41 个场景、74 项断言通过 |
| 源码材料 | 29 个场景、57 项断言通过 |
| NuGet 漏洞 | 包括传递依赖，未报告已知漏洞；完整 JSON 收据保留 |
| Go 模块完整性 | `go mod verify`：all modules verified |
| Go 基础与结构测试 | 选定的 common、CA、资源和 SOCKS 包测试及 Go vet 通过；不是全部上游协议的端到端测试 |
| 新核心安全扫描 | 固定 `govulncheck@v1.9.0`，查询 `https://vuln.go.dev`：No vulnerabilities found，退出 0 |
| 完整 CI | [43683802058](https://github.com/Water-Run/ClashSharp/actions/runs/43683802058)，对应产品提交 `97150e78`：主程序 7,204、安装器核心 4,291、表示层 402、Windows 适配层 1,857，共 13,754，全部通过、零跳过；离线安装包构建也通过。已下载四份实际 TRX，并由 `Test-DotNetResults.ps1` 独立复核及保存摘要 `verified-current-ci-evidence.json` |

覆盖率下限全部成立：Service 81.1%、ViewModel 87.0%、AppHost 68.9%、Model 93.7%、MihomoService 83.4%、RecoveryWatchdog 76.0%、Core 94.8%、Application 90.8%、Infrastructure 88.8%、MCP 93.8%。安装器核心为行 95.73%／分支 92.79%；安装器表示层自身为行 93.61%／分支 89.81%。表示层报告的跨程序集总率包含未被该组测试执行的 Core，不能用来代替表示层自身覆盖率。

## 新核心实测

- **8 组配置校验**：由真实 Clash# 配置生成器输出 Disabled、Standby、RuleTakeover、FullTakeover，每种分别开启／关闭透明代理；新核心 `-t` 全部通过。
- **临时进程联网**：使用独立回环端口，验证无令牌拒绝、准确版本号、direct/global/rule 切换、节点及连接接口、非回环访问拒绝、HTTPS CONNECT 转发。原 GitHub API 测试端点触发公共请求限流后，改用该仓库的静态 `global.json` 核验实际返回内容。测试进程已退出，宿主代理指纹前后一致。
- **Windows 沙盒 TUN 实测**：以应用生成的规则接管配置启动准确的新核心，独立设备 `ClashSharpReleaseProbe` 为 Up，路由建立；禁用 HTTP 客户端代理的 HTTPS 请求成功，TUN 接口流量计数增长。终止自有进程后设备不再活动，客体销毁，宿主代理不变。证据：`tun-sandbox/reports/result.json`、`tun-sandbox/host-result.json`。
- **独立离线交叉编译**：在 Linux x86_64、Python 3.12 中使用最终材料运行随包 `BUILD-OFFLINE.py`。实际使用 Go 1.27.4，关闭 Go 下载，成功产生 Windows amd64 程序；没有执行该 Windows 程序，不宣称逐字节重现。证据：`offline-build-result.json`、`offline-build-info.txt`。

新核心为 `v1.19.34-clashsharp-security`，SHA-256：

```text
0383110f95afa295829f8a91ba92168e9421c2e38accdf0fbb8db9df99b27573
```

## 正式成品与验收

使用 `build.ps1 -PinnedSelfSignedRelease`，不是 Development 或 InternalCandidate 配置。主 MSIX 和 Setup 均由既有长期身份 `CN=WaterRun, OU="Clash#"` 签名，公开证书指纹 `59ED7BC900A39027FBEBCEE33D6AF3AC2A954773`，有效期至 2036-11-14。两份签名均取得 DigiCert SHA-256 RFC3161 时间戳，构建时的 `signtool verify /pa /all /tw` 通过。安装器 Windows 适配程序集的构建策略读回为 `PinnedSelfSignedRelease`。

- 最终 `Setup.exe --verify-payload`：passed，版本 1.0.0.0，4 个载荷文件、7 个机器文件条目，stderr 为空。
- 单独复制成品并篡改公开 CER 的一个字节：同一最终 Setup 的只读入口退出 3，正确拒绝；正式交付目录未改变。
- 干净沙盒 `install-only`：依赖、主包注册、包身份及全部清理通过。
- 原 `launch-no-proxy` 脚本只注册 MSIX，未经过完整安装器建立服务关联；应用报告 `service.provisioning.association_missing`，脚本正确判失败而非把错误窗口当作成功启动。用户批准后已用首轮准确的正式 ZIP 完成真实 Setup 安装：`ClashSharpMihomo` 运行，主窗口稳定 31,325ms，credential/window/pipeline 完成各一次、启动失败为 0，已安装内核摘要匹配；证书只在 TrustedPeople、未进入 Root。客体 `4b6a3d26-bbfa-4b1b-8485-8b40c4c21eff` 已销毁。证据：`full-installer-sandbox-approved-20261115/reports/verified-result.json`、同目录上层 `host-result.json`。这份证据绑定首轮安装器，后续弹窗修复另有原生后台验证，不能混称为最终 ZIP 的再次人工安装。

交付文件：

```text
artifacts/deliverables/20261115-release/Clash#.zip
artifacts/deliverables/20261115-release/Clash#.zip.sha256
```

ZIP 为 **471,655,715 字节**，SHA-256：

```text
9fcf2b895da7151ea95074a83ca2b59ac40a5e1b08af409755af15ffe3fe425b
```

Setup SHA-256：

```text
17f37fada983268147a5ed4c6ed4760d370f7c5912c6f3ba11c63f4be5c008d0
```

ZIP 内共 10 个文件，已逐项解压流核对长度及 SHA-256；最外层仅有 `Setup.exe`、`LICENSE`、`README.md`，以及 payload、licenses、metadata 子目录。没有 PFX、私钥、调试符号或开发版标识。`release-delivery.json` 保存完整文件清单。

## 安装与完成边界

完整解压 ZIP，再运行其中的 Setup.exe。包版本维持 1.0.0.0；若目标机已有内容不同的旧 1.0.0，先在旧版导出备份，用旧版对应的 Setup 卸载，再安装此包并导入。旧正式包完整保存在 `artifacts/release-check-20261115/previous-signed-release/`，可供对应旧版本卸载使用。

继续沿用已接受的自签名限制：在未信任该证书的机器上，管理员提示可能显示“未知发布者”；安装器自身的固定指纹不能防御整份安装器被另一份自签名程序替换。私钥和签名身份没有轮换。完整安装器会按需管理软件包的 TrustedPeople 证书；只读校验不改变证书存储。

此前已接受的 UI 取舍、一次未解释的旧模式切换及 WinUI 胶水覆盖率盲区仍见上一轮记录。本轮没有重拍或逐页重新验收全部语言。第三方材料校验没有扩大既有归属材料声明的来源追溯范围。供用户连续数天使用的体验验收仍由用户完成；通过后可直接发布本次冻结的 ZIP 字节。
