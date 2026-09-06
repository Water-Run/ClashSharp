# ClashSharp 1.0.0 安装器构建

安装器采用 WPF，主程序采用 WinUI 3。Installer Core 定义事务与权限协议，
Presentation 管理页面状态，Windows 适配器负责包、服务、证书和受保护状态。
正常安装目标仍为 Windows 11 原生 x64。

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

当前生产安装执行开关仍默认关闭，完整 Windows 11 安装、修复、升级、卸载、
跨用户关联与故障恢复矩阵尚未完成；开发包不能被提升为 1.0.0 正式发布。
最新结果见 [执行账本](../../docs/reviews/1.0.0-execution-ledger.md)。

## 上游数据来源

GeoData 使用 [MetaCubeX/meta-rules-dat 固定数据提交](https://github.com/MetaCubeX/meta-rules-dat/tree/8b2495481407ab931fbc201a245f1982b60b5a70)。
生成与归属信息见固定的
[上游说明](https://github.com/MetaCubeX/meta-rules-dat/blob/4178770badecb1b349fbcd62c737e0d7a2079729/README.md)
和 [仓库许可](https://github.com/MetaCubeX/meta-rules-dat/blob/4178770badecb1b349fbcd62c737e0d7a2079729/LICENSE)。
该数据汇总了多个上游；正式发行仍须随候选归档相应来源与第三方许可，
不能把生成器仓库的许可视为全部数据的唯一许可。
