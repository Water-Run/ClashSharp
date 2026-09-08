# ClashSharp 1.0.0 安装器构建

安装器采用 WPF，主程序采用 WinUI 3。Installer Core 定义事务与权限协议，
Presentation 管理页面状态，Windows 适配器负责包、服务、证书和受保护状态。
正常安装目标仍为 Windows 11 原生 x64。

## 单页安装与维护

界面采用简化的 VS Installer 布局：一个 ClashSharp 产品卡片显示版本、当前状态和
可执行操作。未安装时提供安装，已安装时提供修复与卸载，有未完成事务时提供继续操作；
按钮由运行端口的检查结果决定。进度仅在操作进行时显示，检查结果和可复制的诊断代码
放在默认收起的“安装详情与诊断”中。关闭窗口会请求取消并等待已开始的任务收尾。

安装器为 WPF 自包含 EXE，无需预装 .NET，也无需单独安装安装器本身。主程序仍是
Windows 管理的 WinUI 3 应用包，代理服务使用系统目录；这里的绿色运行指安装器入口，
不表示主程序或服务无需注册。

仓库当前没有 Rust/Slint 安装器源码、Cargo 清单、锁文件或 Rust 工具链入口，
旧的空 src、ui、tests 目录也已清理。Logo.png 与 LogoInstaller.png 是
品牌重建的设计参考，保留用于追溯；运行时使用 WPF 矢量资源和生成的多尺寸 ICO。

## 离线输入与开发构建

在独立的 Windows 构建机使用 PowerShell 7 和仓库固定的 .NET SDK 10.0.201：

    dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
    ./Tools/Get-ReleaseInputs.ps1
    $inputRoot = Join-Path $PWD 'artifacts/release-inputs/1.0.0'
    $assets = @('Country.mmdb', 'GeoIP.dat', 'GeoSite.dat', 'ASN.mmdb') |
        ForEach-Object { Join-Path $inputRoot $_ }
    ./Tools/Prepare-GeoData.ps1 -AssetPath $assets
    ./ClashSharp/Installer/build.ps1 -Development

开发构建会在构建用户的 My 证书存储创建开发 MSIX 签名证书，不执行安装、不信任证书，
不更改系统代理。请在 CI 或隔离构建机运行。已有 PFX 需要导入时，通过进程环境变量
CLASHSHARP_CERTIFICATE_PASSWORD 提供密码；脚本在启动子进程前清除该环境变量。

release-inputs.json 固定四项 GeoData 的上游提交、长度和 SHA-256，以及
Windows App Runtime x64 包的长度、摘要和签名者。下载脚本使用提交地址；
缓存损坏、输入更新或依赖漂移会终止构建，更新输入需要代码审查。
NuGet 包和 SDK 由各项目锁文件与 global.json 固定。

构建会离线生成 THIRD-PARTY-NOTICES.zip，覆盖主程序、服务、恢复进程和安装器的
NuGet 构建/运行输入与 SDK 下载依赖。生成器用 SDK 自带的 NuGet 读取器验证实际
包的内容摘要和签名包内容完整性，再与锁文件及固定下载摘要比对；不会通过复制
缓存侧车文件来代替内容校验。许可、通知和 nuspec 元数据保留原始字节，缺少包内
许可原文的已知依赖使用 ThirdParty/catalog.json 中精确匹配的来源快照。

同一份说明 ZIP 随安装器提供，并包含在主 MSIX 的 ThirdParty 目录内；打包会读取
最终 MSIX 确认其内部副本的摘要和文档引用。payload-provenance.json 记录它的
摘要、长度和输入数量。说明包中的 inventory.json 区分依赖来源并记录对应项目，
不包含构建机的 NuGet 缓存或用户目录路径。

MSIX 内携带 .NET 10 运行时；WinUI 使用随安装器携带的独立微软
Windows App Runtime framework MSIX。构建直接检查最终主包中的 runtimeconfig、
RID、CLR/host 文件和原生 x64 PE 头，再验证依赖的身份、签名、时间戳与固定摘要。
服务和恢复进程分别发布为自包含单文件，通过独立 staging 校验后进入主包。

生成目录为 artifacts/installer/release/。使用时保留 EXE 与相邻 payload 目录；
EXE 的“自包含单文件”指安装器运行环境，安装载荷仍在该目录。开发文件名明确含
Development-Unsigned，另附标识文件；它不是正式发行版。

打包结束会启动最终 EXE 的 --verify-payload 只读入口，实际加载自包含运行时与
嵌入清单，验证相邻 payload 的精确文件集合、长度、摘要、MSIX 身份与内部
machine 文件，然后释放全部文件句柄。成功时退出码为 0，重定向 stdout 可取得
一条 JSON 收据；失败为 3，保留参数组合错误为 2。该入口不创建 UI，不提权、
安装、信任证书或修改服务；它不代表 EXE 签名、目标平台和安装事务已经验收。
正式提权入口只接受 ClashSharp-Installer.exe，开发标识文件名不能获得安装权限。

CI 的 Offline Installer package 在干净 Windows runner 上执行以上完整打包，
产物保留五天供检查。此检查验证构建和载荷，不代表 GUI 安装或故障恢复已经验收。

## 正式签名与启用

不带 -Development 的构建另外需要受控的 MSIX PFX/CER、精确的
CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT、可用的 Authenticode 私钥、
CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT、
HTTPS CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL 和固定
CLASHSHARP_WINDOWS_SDK_VERSION。脚本验证签名与时间戳后才产生正式文件名。
可选的 CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT 必须与仓库固定输入一致。

正式签名构建现在显式编译生产安装与 helper 入口；`-Development` 显式关闭该入口，
普通项目构建仍使用预览运行时。嵌入清单、签名者固定与可信时间戳校验继续决定
正式文件能否输出，重命名开发文件不能启用安装权限。CI 用真实 MSBuild 评估三个
构建配置，并检查缺少正式构建标志或嵌入清单时会拒绝启用。

Windows 11 Sandbox 已通过生产 parent/helper 的安装、修复和卸载事务，卸载后
包、服务、安装器拥有的两类证书及服务目录均已移除。该验证使用一次性客体信任和
明确记录的客体目录 ACL 夹具；WPF 页面操作、升级、跨用户关联与完整故障矩阵
仍需完成，开发包不能被提升为 1.0.0 正式发布。
最新结果见 [执行账本](../../docs/reviews/1.0.0-execution-ledger.md)。

## 上游数据来源

GeoData 使用 [MetaCubeX/meta-rules-dat 固定数据提交](https://github.com/MetaCubeX/meta-rules-dat/tree/8b2495481407ab931fbc201a245f1982b60b5a70)。
生成与归属信息见固定的
[上游说明](https://github.com/MetaCubeX/meta-rules-dat/blob/4178770badecb1b349fbcd62c737e0d7a2079729/README.md)
和 [仓库许可](https://github.com/MetaCubeX/meta-rules-dat/blob/4178770badecb1b349fbcd62c737e0d7a2079729/LICENSE)。
该数据汇总了多个上游。说明包现在保留上述固定提交的 README、LICENSE 和生成
工作流，并将它们绑定到四个数据文件；生成工作流仍引用了会变化的上游分支。
这组快照不能重建当时全部上游输入，也不是完整的对应源代码发行包。正式发行仍须
补齐相应源代码与上游数据许可归档，不能把生成器仓库的许可视为全部数据的唯一许可。
