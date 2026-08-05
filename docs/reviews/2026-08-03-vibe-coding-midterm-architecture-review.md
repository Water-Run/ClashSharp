# ClashSharp Vibe Coding 中期架构与功能校正审查

- 审查日期：2026-08-03
- 实施收口：2026-08-05
- 历史审查基线：`main@b45e6be`；Clash Verge Rev `903410c12`、Clash Nyanpasu `6ea1bf1`
- 决策收口复核：Clash Verge Rev `a8275b20`、Clash Verge Service IPC `10edb52e`、Clash Nyanpasu `a58f00b6`、Nyanpasu Service `eb9a3f9`
- 范围：运行时所有权、配置控制面、Profile/订阅、TUN、系统代理、Connections、日志、更新、生产装配和测试可信度
- 限制：本轮没有执行真实安装、更新、卸载、Service 注册、TUN、路由、DNS、WinINet 或注册表写入；源码、XAML 编译和自动化测试不能替代后文的 Windows 人工矩阵
- 实施状态：用户已逐项确认 1A、2B、3A、4A、5A、6A、7A、8A；下文“当前实现”优先，后半部分“审查基线”仅保留为实施前证据

## 决策后实施结果（当前工作树）

原审查识别的两个数据面 P0，以及收口时新增发现的 Service controller 权限、App controller 身份、mixed TCP 假就绪和 Installer machine+MSIX 混代问题，均已按决策方向完成源码修复；更新、恢复、provider/geodata、诊断和 Installer/App 的源码边界也已收敛。8A 选择的不是跨版本自动回滚，而是 Installer 独占的 durable two-phase roll-forward：机器级 `Prepared` 意图先于 Add-Appx 持久化，package 与 machine 分别由提升 helper 独立复验后单向前进，任何不确定状态保留公开 marker 并要求同一 Installer Repair；App 只读 marker 并在 Core、Service、系统代理和透明代理（TUN）前阻断。本轮在缺少 release-only 外部输入时可执行的、无系统 mutation 的源码/构建/测试门禁已经全部通过，结果见文末；正式 packaging/signing 未运行。正式发布仍依赖仓库外受控提供的 GeoData 与签名材料；ProgramData/ProgramFiles handle-based 路径防护、旧版无 startup barrier 的跨 SID 迁移策略，以及真实 AppXSVC/SCM/UAC/断电 cut-point 验证仍是明确的工程发布阻断。完成这些条件后才可以进入 Windows 真机集成验收。在真实 LocalSystem、Installer、TUN、注册表、崩溃恢复和 UI 矩阵完成前，仍不能称为稳定发布候选。

| 范围 | 当前实现 | 判断 |
|---|---|---|
| Core owner / TUN | 建立 `None / App / Service` 互斥所有权；Disabled、App↔Service 切换、SCM pending/unknown、双 owner、requested/effective TUN 均 fail closed；产品语义明确“透明代理 = Mihomo TUN” | 原双 Core P0 已关闭；真机切换仍是发布门禁 |
| App↔Service 控制面 | v2 Named Pipe 按用户 SID 派生并使用显式 DACL，所有请求均校验版本/token；controller broker 另要求 exact session/generation/hash，Start/Reload 要求 exact generation/hash。App 客户端使用 Anonymous、non-inheritable handle，连接后在首字节前用 `GetNamedPipeServerProcessId` + `QueryServiceStatusEx` 要求 pipe server PID 等于固定、Running、own-process 的 SCM 服务 PID | 稳定 pipe 名被抢占或 token 可见时也不能伪装 Service |
| Service 私有 controller | Service 为每个 effective generation 注入不经 App IPC/用户配置暴露、仅写入 SYSTEM-owned staging 的随机 pipe/secret，Mihomo pipe 仅授予 SYSTEM；Service 在发任何 HTTP 字节前校验 pipe server PID 等于 Job root，并对每个请求发送 private Bearer。Running 必须通过 `/version`、`/configs` 和 exact TUN/mode/`mixed-port:0` readiness | LocalSystem controller 权限 P0 已关闭 |
| Service controller broker | App 只能调用固定类型命令：effective probe、connections 读/关、proxy/provider 快照、已验证组内 proxy 选择、按 `kind + name` 更新一个 provider、子进程 runtime logs 和脱敏 Service-host log snapshot；没有任意 HTTP 转发，也不开放 `/configs` 写、storage、upgrade、restart、DNS/debug/UI | provider update 与诊断可用，但用户不持有或间接获得完整 LocalSystem controller 权限 |
| App-owned controller / mixed listener | 固定 `127.0.0.1:9090` 建连后、发送 Bearer/HTTP/WebSocket upgrade 前，用 `GetExtendedTcpTable` 完整反向四元组绑定到带 epoch 的 App Job root PID；连接不跨代复用。`/configs` 匹配后还独立要求 mixed TCP LISTEN 行属于同一 PID并前后复核 epoch | 伪 controller 与 Mihomo UDP-bind stale-pointer 假就绪已关闭；端口占用现在只会 fail closed |
| 进程所有权 | App 与 Service 均使用 suspended create→assign Job→resume；每代独立 kill-on-close Job，Stop、失败和异常退出必须证明整个 Job tree 为空才释放 owner，无法证明时保留 fault 并拒绝 replacement | 源码级 crash handoff 已收口；仍需真实崩溃/关机验收 |
| Runtime config transaction | YamlDotNet AST 合成；对 exact candidate 执行 semantic guard 和 `mihomo -t`；持久 desired/applied generation、hash 与 activation plan；exact authenticated readiness 后才 commit；保留五代快照并支持恢复，回滚失败显式化 | 原运行配置 P0 已关闭 |
| Mode / port / TUN / DNS / Profile | Settings、托盘、Trigger、主控、Profile 激活和活动订阅更新统一进入 mutation admission、fair gate 与 runtime transaction；缺失 Profile source fail closed；TUN 缺省 DNS 与组合校验已加入 | 主生产路径已收敛 |
| Profile / subscription | 增加重命名、删除、启停、周期编辑、自动调度、single-flight/backoff、活动 Profile 安全 fallback、版本历史/回滚和 UI；catalog cache 仅在 durable save 后发布，删除失败会补偿 runtime 与 active pointer | 原 P1/P2 产品缺口已补齐主体 |
| Windows mutation | WinINet 完整 tuple、Terminal/WSL 环境变量、`WSLENV` 和 Store loopback exemption 均使用带 baseline、owner、Applying/Applied phase 的 durable journal；仅在当前值仍属于 Clash# 时恢复，并兼容 v1 journal | 原 P1 已关闭；需真机验证 PAC/企业策略 |
| Core crash / watchdog / readiness | “进程/SCM running”“controller authenticated”“effective plan matches”分离；同用户、非提权、one-shot Recovery Watchdog 以 exact PID + creation time + lease 等待 GUI，异常退出后立即恢复 journal-owned WinINet，正常退出先撤销 lease；下次启动 reconcile 仍是第二道保险。Service 对同一 immutable generation 有界重启，耗尽后 Faulted | 2B 已落地；需真机 kill/power-loss 验收 |
| Connections / Logs | App owner 通过 PID-bound authenticated `/logs` WebSocket；Service owner 通过 fixed broker 1 秒轮询，其中 child stdout/stderr 与 Service-host 日志均由 Service 汇总。消息/列表有上限，离页取消；Logs 页面合并 App/SQLite、当前 Core runtime 与 Service-host ring snapshot，不展示 raw controller response | 7A 的最小页面内 Core + Service 诊断已落地；Service authority 的 child/host 日志在 Service 侧有界并脱敏，App owner 的 controller 日志有界规范化；持久化/上传仍明确不做 |
| 导入 / Reset / 清数据 | Import/reset 使用跨实例文件锁、校验 manifest 与 retained WAL receipt；启动先恢复 cleanup。durable settings 只在 UI/启动/采样/网络运行态激活成功后 commit，失败 rollback 并补偿外部 baseline；clear-all 先 drain runtime/admission，再删除并强制请求重启 | 断电/kill 与副作用边界提升为可恢复事务 |
| Settings 写入线性化 | `AppSettingsService` 继续作为唯一生产 authority；普通 setter 取得 ordinary lease，destructive import/reset 与启动恢复使用同一 exclusive lease，批量写入使用 admitted editor；barrier 关闭后新 setter fail fast，已进入 setter 先 drain | 1A 已落地；关闭跨代覆盖竞态且没有第二真相源 |
| Provider / GeoData / 文件边界 | source bytes/hash 不变，effective YAML 进入 SYSTEM-owned protected staging；inline provider 直接使用；`http/https` provider 仅接受有界 absolute URL、无 userinfo，并重写到受保护的相对 cache path，由 Mihomo 下载；file provider 只接受 authenticated config root 下安全相对路径、普通非 reparse 有界文件，复制校验后重写。GeoIP/GeoSite/ASN 只从 Installer-owned `GeoData/manifest.json` 与声明的 SHA-256/length 资产按需 stage；缺失/损坏返回稳定 repair code；用户 `geox-url` 和 geo auto-update 继续拒绝 | 4A 的代码契约和定向门禁已落地；仓库当前没有发布所需的四项真实 GeoData 资产，正式构建会 fail closed，因此常见 geodata 兼容仍等待外部 pinned release 输入与真机验收 |
| App 更新 / 独立 Installer | App 只向编译期固定的 GitHub Releases latest API 发 5 秒、64 KiB、禁重定向的有界 GET，比较稳定数字版本并使用内存 ETag；只显示 current/available/unavailable，且只打开编译期固定 release 页面，不下载、不执行、不安装。Settings 不再提供 Service Deploy/Uninstall。独立 Installer 先在 `%ProgramData%\ClashSharp\Installer\transaction.json` 原子持久化 strict `Prepared` journal，再由非提权目标用户部署 MSIX；提升 helper 不信任 parent 回执，而是重新查询固定 SID 的 identity/publisher/x64/full-name/version 并校验注册包 machine subset，随后单向推进 `PackageCommitted → MachineCommitted → Verified`。machine commit 使用 transaction-id 派生的稳定 staging/backup；首项可见 machine mutation 是 Stop Service，紧接 `disabled` 与仅 SY/BA 可控制的 SCM fence，之后才交换 payload/SCM/association；成功复验、清 staging/backup 后才删除 public marker。Add-Appx 失败/parent 或 helper 中断均不猜测回滚，marker 保留并要求同一 Installer Repair；CurrentUser package child 与 machine script 都在 kill-on-close Job 中。App 取得 package-independent startup barrier 后只读 marker，Pending/Invalid 时不 arm Recovery Watchdog、终态关闭 mutation admission，并在 Core/Service/系统代理/透明代理（TUN）前显示六语言 Repair 提示 | 3A + 8A 的传统软件 authority 和 durable roll-forward 已落地；不会再产生“machine 先升级、MSIX 后失败却放行 App”的已知路径。没有用户可选 previous-version rollback；旧版 App 若尚不支持或从未建立 startup barrier，跨 SID Repair 继续安全拒绝。真实 AppXSVC ack-loss、SCM cut、alternate-admin、多 SID 与断电收敛仍须 VM gate，不能由绿色源码测试代替 |
| 诊断 / 冲突 | Service/config/provider/geo/controller/mixed，以及启动期 TUN/系统代理冲突使用有界稳定 code + 六语种可操作消息；IPC incomplete frame timeout 收紧为约 5 秒并区分 endpoint occupied。启动检查只读枚举已运行的已知 TUN 接口并报告名称/代码，TUN 接口项没有自动修复；既有显式进程/系统代理修复仍需用户触发 | 7A 的最小稳定诊断已落地；深层 route/DNS 归因仍属真机/VM 后续项，不做激进自动清理 |
| 部署与威胁模型 | 一台机器只支持一个当前 ClashSharp/Core owner 和一个 Installer 关联的交互用户；TUN/route 是机器级，不承诺 RDS/多账户流量隔离。固定 pipe + SID DACL/token/session/generation + SCM/PID 首字节门禁 + 窄 broker 保护完整性；同 SID starvation/name-squat 记为可用性限制 | 5A/6A 已收口；不引入 WFP、随机发现协议或敌对本地账户复杂度 |

4A 的兼容方向来自当前成熟客户端的实际边界，而不是照搬其全部权限模型：Clash Verge 当前分支会建立 [Service private runtime bundle](https://github.com/clash-verge-rev/clash-verge-rev/blob/a8275b20b73bd95a9d38b96c73770a897cf40b8f/src-tauri/src/core/runtime_bundle.rs#L16-L166)，其 Service IPC 会[复制 local providers/geo assets](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/10edb52ec071520f14ce78654e401e4246127d75/src/core/runtime_generation/assets.rs#L249-L290)，同时保留 Mihomo 下载 HTTP provider；Nyanpasu 也使用 [SID ACL 与 safe path](https://github.com/libnyanpasu/nyanpasu-utils/blob/3cb3af02222ced3972d95ade599949098b159202/src/core/instance.rs#L128-L173) 并开放[窄 provider update API](https://github.com/libnyanpasu/clash-nyanpasu/blob/a58f00b6ceb7c5d049b196516f14b82383d784ee/backend/tauri/src/core/clash/api.rs#L337-L367)。ClashSharp 在此基础上保留自己的 exact generation、protected staging、PID identity 和 typed broker 门禁。

App listener 身份门禁来自确定的上游行为，不是泛化猜测：Mihomo controller 以 goroutine 启动，TCP bind 失败只记录日志并返回，主进程仍可继续运行（[`ReCreateServer` / `start`](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/hub/route/server.go#L92-L187)）；mixed listener 则先建 TCP、再建 UDP，UDP bind 失败会关闭 TCP 却保留非 null 的 listener 引用（[`ReCreateMixed`](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/listener/listener.go#L443-L492)），而 `/configs` 的端口投影只按该引用读取地址（[`GetPorts`](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/listener/listener.go#L656-L688)）。因此只看进程存活、Bearer 或 `/configs` 都不足以证明真实 listener；本轮才增加 controller established 四元组和 mixed LISTEN PID 两道独立证明。

本次没有把已经存在的 `ISettingsRepository` / `DataGenerationManager` 强行接入生产。`SettingsEnvelopeEditor` 已存在，但当前 legacy `AppSettingsService` 仍是实际 authority；现在通过同一 admission barrier 与 retained WAL 获得可证明的写入边界。生产层仍缺少异步 change-set API、LocalSettings→envelope 的一次性迁移/回退协议，以及 Profile、Trigger、日志等直接路径在 generation 切换后的 repository rebind。仅在同步 property setter 后追加 envelope 双写会制造第二个真相源，因此 architecture gate 继续禁止未接线 repository 进入生产 composition。

Service 的 protected runtime 不能单独证明整个 LocalSystem 配置面安全。Mihomo 的 safe-path 实现对 HomeDir 做 [`filepath.Rel` / `IsLocal` 词法判断](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/constant/path.go#L74-L98)，所以用户可写目录中的 reparse 不能作为权限边界；当前实现用 SYSTEM-owned staging、最小环境、独立 schema guard、私有 controller、PID-authenticated fixed broker 和 ordinary-file/reparse 检查组合收口。HTTP provider 保留成熟客户端常见的 Mihomo-native 下载模型，但 Service 决定唯一受保护 cache path；file provider 在启动前从认证配置根复制到 protected generation。App→Service 不传 provider bytes/hash，也没有任意 controller passthrough。

禁止用户 `geox-url` 本身并不足够：Mihomo v1.19.29 的 raw config 仍内置 [GeoIP/GeoSite/MMDB/ASN 下载地址](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/config/config.go#L569-L582)，其解析流程明确依赖 geodata 的[加载与下载](https://github.com/MetaCubeX/mihomo/blob/v1.19.29/config/config.go#L630-L633)，资产缺失时的实际下载也有[上游复现](https://github.com/MetaCubeX/mihomo/issues/1572)。当前源码要求正式 Installer 构建由外部受控输入提供 `Country.mmdb`、`GeoIP.dat`、`GeoSite.dat`、`ASN.mmdb` 四项完整的声明式 bundle；缺少任一项、存在未声明文件或 length/SHA-256 不符都会令构建 fail closed。仓库当前未包含这些真实资产。资产齐备后，Service 发现 exact configuration 的 GEOIP/GEOSITE/IP-ASN/DNS 隐式需求时，只复制所需的 manifest 声明资产并再次验证 length + SHA-256；运行时缺失或损坏会提示运行 Installer Repair，不让 LocalSystem 静默走内建 geo 下载。

另一个不能靠 pipe ACL 解决的结构事实是：Windows TUN/route 属于机器级数据面。Service 使用目标用户的配置和节点时，同机其他交互用户的流量也可能进入该 TUN，并出现在 connections/broker 视图中。5A 已明确把产品部署模型限定为一台机器一个 Installer 关联的交互用户；第二用户接管和 RDS 并发不在当前支持范围。未来若改变产品承诺，必须先加入 WFP/per-session 策略，不能把当前 pipe ACL 当作流量隔离。

这里仍有两项无需产品选择、但必须保留在发布安全门禁中的工程收口：当前 ProgramData/ProgramFiles 首次创建与替换仍含 path check→create→ACL，transaction DACL 已校验为 protected 且非管理员无 write/delete，但最终仍应使用原子安全创建、canonical ACL 校验和拒绝 rename/delete 的目录 handle 关闭路径式 TOCTOU；Service YAML guard 覆盖当前审计到的高危 schema，但不能宣称穷举 Mihomo 未来新增字段。前者是正式发布阻断；两者都要通过真实 LocalSystem、MSIX ACL、junction/race 的攻击性验收。8A 已把 machine+MSIX 跨阶段策略明确并实现为 durable roll-forward，因此不再把“缺少可回滚两段式事务”列作未决设计；它仍必须通过真实断电 cut-point 证明实现收敛。

发布信任还必须与源码内的内容完整性区分：Installer 已把 whole sibling payload 以及 MSIX 中将复制到机器范围的文件集合、length 和 SHA-256 锚定进自身，并对最终 MSIX 内四项 GeoData 的 canonical 名称、manifest exact schema、length 与 SHA-256 再次逐项绑定；这些 anchor 仍只能证明“payload 与该 Installer 匹配”，不能单独证明发布者身份。正式构建现要求显式 pinned Mihomo version + SHA-256、四项 exact GeoData、受控 MSIX signing certificate，以及带可信 timestamp 的 Installer Authenticode 签名；真实证书、私钥和 GeoData 都是仓库外 release 输入，不得以自动生成的 development certificate 代替。依赖装配使用 `dotnet publish --no-restore` 与 Cargo `--frozen`，固定 `x86_64-pc-windows-msvc`；PFX password 在任何 build child 前从环境清除。Cargo 未签名输出只进入 disposable staging，签名和验证成功后才 promotion 到独立 `release-artifacts`，失败清理 staging/final output。SignTool 只能从显式 pinned Windows Kits x64 路径取得，并要求 Windows trust 状态 `Valid`、Microsoft signer 与 RFC3161 timestamp；不能用“签名证书今天是否仍未到期”错误否定已经可信时间戳的 Microsoft 工具。旧 `Tools/build_installer.py` loose-publish 入口已删除，`ClashSharp/Installer/build.ps1` 是唯一正式打包入口。

### 用户决策 1A–8A（决策已定，源码边界已收口）

| 决策 | 已选方向 | 已落地边界 |
|---|---|---|
| 1A — Settings authority | 本版本继续使用传统、单一的 `AppSettingsService` 生产 authority | admission + retained WAL 线性化现有写入；architecture gate 禁止 envelope shadow write。未来若迁移，必须一次完成 migration/change-set/rebind/旧路径删除 |
| 2B — 崩溃恢复 | 增加同用户、非管理员、one-shot Recovery Watchdog | GUI crash 后立即恢复 journal-owned WinINet；正常退出通过 exact lease disarm；启动 reconcile 保留为兜底，不做常驻提权服务 |
| 3A — 传统软件更新 | 独立 Installer 是唯一安装状态 authority；App 只提示 GitHub stable release 状态 | App 不下载、不执行、不安装、不修复、不迁移 Service；只访问两个固定 GitHub URL。新 release 的 Installer 负责 install、in-place update/repair、uninstall、Service migration/关联和 GeoData；App 仅以只读启动 gate 响应 Installer pending state |
| 4A — Provider / GeoData | 与成熟客户端对齐的简化模型 | inline 可用；HTTP(S) 由 Mihomo 下载到 Service 指定 cache；file provider 从认证根安全复制；仅开放 typed `UpdateProvider(kind,name)`；GeoData bundle + manifest/hash 的代码契约已完成，禁用自更新/自定义 geox URL；四项真实资产仍须由外部 release 输入提供 |
| 5A — 部署模型 | 一台机器一个当前 ClashSharp/Core owner，只支持一个 Installer 关联的交互用户 | 不宣称多会话流量隔离；第二用户不得无声 takeover，owner 变更属于 Installer 显式迁移；本版本不做 WFP/per-session |
| 6A — 安全范围 | 主流桌面 threat model | 保留固定 pipe、SID DACL/token/session/generation、SCM/PID 首字节 gate 和窄 broker；5 秒 incomplete-frame timeout + endpoint occupied 诊断。同 SID starvation/name-squat 是已接受的本地可用性限制，不新增随机发现协议 |
| 7A — 发布最低诊断 | 稳定版所需的最小诊断、日志和真实 Windows 门禁 | stable typed failure code、六语种行动提示、有界 Core 日志、对 service authority secret 脱敏的 Service child/host 日志、只读 TUN 冲突提示已实现；Installer/Windows 人工矩阵已定义但尚未执行。不做自动删路由/修 DNS、上传诊断或持久 Service 日志 |
| 8A — Installer 跨阶段一致性 | 本地、durable two-phase roll-forward；Installer 单独持有恢复 authority，App 只读阻断 | strict bounded journal 使用可信 MSIX version + whole-file hash 绑定同一 release；Prepare 必须先持久化，helper 独立验证 package 后才 `PackageCommitted`，machine commit 先 fence 旧 Service 再幂等前滚；Verified/cleanup 后 marker 最后删除。任何 Add-Appx 不确定结果保留 marker，不做猜测性 rollback；App 不 arm watchdog、不启动 Core/Service/系统代理/透明代理（TUN），只提示用同一 Installer Repair |

### 已接受的延后项与当前限制

- **Settings generation 迁移**：1A 明确保留 `AppSettingsService`；envelope migration 不是本版本内的隐性待办，更不能以双写方式偷偷接入。
- **TUN 深度修复**：当前会只读报告已运行的已知 TUN interface；route、DNS hijack、外部 service/controller 的进一步归因，以及自动清理 route/DNS 均延后。
- **Service 日志扩展**：页面已有有界脱敏 Service-host ring；跨重启持久化、导出包自动收集和上传服务均不做。
- **App controller pipe 统一**：App TCP controller 已做连接后首字节前 PID 认证，fixed 9090 被占只会拒绝启动/commit，不再泄露 Bearer 或误开 WinINet；仍可迁移到 current-user SDDL 的随机 named pipe，以消除固定端口可用性冲突并统一两种 owner transport。
- **内部命名迁移**：用户语义已按“透明代理 = Mihomo TUN”处理，并区分 requested/effective；为避免设置迁移风险，`TransparentProxyEnabled` 等兼容字段名本轮未整体重命名。
- **多用户/WFP**：5A 明确不支持 RDS/多账户并发 TUN；若以后扩展，这是新产品里程碑，而不是当前 ACL 的小修补。
- **本地可用性对抗**：6A 接受 stable endpoint 的 name-squat 和同 SID starvation 风险；当前门禁保护的是特权完整性和 secret 不在伪 server 首字节前泄露，不承诺抵抗同用户主动 DoS。
- **HTTP provider 内容固定**：4A 选择 Mihomo-native 下载，不建立 App→Service provider bytes/hash 协议；URL/path/控制面受限，但远端内容可用性与 TLS/upstream 信任仍需按常规 provider 风险处理。
- **应用内安装动作**：App 中没有 Service 部署/卸载、更新下载或 Installer 启动捷径；用户按提示独立运行 Installer，属于刻意产品边界而非缺失按钮。
- **旧版本跨用户迁移**：elevated helper 不会替不认识 startup barrier 的旧版 App 创建一个“看似安全”的锁；旧 owner 缺少该锁时跨 SID Repair 会 fail closed。发布前需确定 barrier-aware 最低版本、先升级旧 owner 或重启后离线迁移路径，并在 VM 中验证错误提示与恢复步骤。
- **历史版本回滚**：8A 只保证同一 release 的确定性前滚，不提供用户选择 previous version、下载旧 asset 或自动降级；这些若以后加入，必须另建签名、版本和数据迁移协议。

## 审查基线结论（实施前，保留作证据）

> **历史基线声明**：从本节到“原建议实施顺序”描述的是 2026-08-03、`main@b45e6be` 的实施前问题与当时建议。它们用于解释为何修改，不代表当前工作树仍存在同名缺陷；若与前文冲突，以“决策后实施结果”和 1A–8A 为准。

当前代码已经具备较完整的 MVVM、启动协调、持久 mutation、退出补偿和自动化测试基础；问题不是“没有架构”，而是**新旧运行时模型并存，desired state、applied state 和实际 Core owner 尚未收敛成一个事务系统**。

当前工作树不应直接作为稳定版发布候选。存在两个明确的 P0 数据面阻断：其一是“透明代理（即 Mihomo TUN）”的双 owner，服务部署后不会启动，手动启动后服务和 GUI 又会各自启动一份 Mihomo；其二是活动 Profile、活动订阅更新和多项运行设置只改变 desired state，当前 Core 继续使用旧配置。前者会争抢配置、端口、controller 和 TUN，后者会让 UI 声称切换成功而实际路由不变。

本轮已落地五组高置信校正：

1. 将 GUI / app-owned Core 的 controller endpoint、LAN 监听和 bearer secret 提升为 App-owned runtime authority，阻止普通订阅配置夺取该控制面；
2. 恢复 Connections 的主导航、路由、托盘入口和页面首刷生命周期；
3. 移除 Connections 将 Mihomo 累计字节手工写入 delta 统计库的入口，避免与后台采样重复计数；
4. 生成态 runtime config 不再导出，旧 v1 包导入时会忽略该衍生文件，clear-all 会轮换内部 controller 凭据；
5. 六语种 Profiles/Links、Windows repair、URL masking 和启动冲突描述，以及中文 README 的 startup restore helper 定位，均收敛到当前真实能力：不再把自动更新、可编辑周期、回滚、可逆恢复、网络 URL 阻断或 TUN 冲突探测写成已完成功能，也不再把一次性 helper 称为常驻服务。

## 名称和实际语义对齐（审查基线，实施前）

| 基线名称 | 基线实际语义 | 审查口径 / 建议 |
|---|---|---|
| 透明代理 / `TransparentProxyEnabled` | Mihomo `tun:` | 就是 TUN，不另计一个“缺少 TUN”功能；拆为 `TunRequested` 与 `TunEffective` |
| 系统代理接管 | WinINet manual proxy | 不是透明代理；只覆盖支持系统代理的应用流量 |
| `CurrentMode` | 用户期望模式 | 当前并不总等于运行模式；改为 `DesiredMode` / `AppliedMode` |
| `ActiveProfileId` | UI/持久化选择 | Profile 切换后当前 Core 不会立即更新；改为 desired/applied 两个状态 |
| `RestoreProxyOnExit` | 当前仅关闭 Clash 代理 | 没有恢复接管前完整 WinINet 状态；修复语义或临时改名 |
| `MihomoServiceStatus.IsInstalled` | 服务注册存在 | 不代表 TUN 可用；需要 Installed / Running / Ready / ControllerReachable |
| `MainlandChinaUrlBlockingEnabled` / “URL blocking” | UI 显示文本中的 URL 遮罩 | 不注入 Mihomo 规则，也不阻止网络访问；本轮已校正六语种用户文案，内部名称建议迁移为 `MarkedUrlMaskingEnabled` |
| Logs | App/SQLite 日志与统计库 | 不是 Mihomo 实时日志；Core 运行期输出目前不可见 |

## 架构判断（审查基线）

值得保留并继续演进的部分：

- 单实例仲裁已前置，启动步骤、runtime participant、退出 drain/补偿和 durable mutation 已有清晰骨架；
- 页面 ViewModel 已大量使用窄接口，数据页具备 Loaded/Unloaded 生命周期；
- Trigger 的持久化、调度和 recovery 已形成较完整的垂直切片；
- 下载具有 timeout、大小上限和 staging，Profile 导入也已有局部验证/回滚；
- Release x64 构建与大规模自动化测试是有效的基础门禁。

需要校正的总体结构：

```text
用户操作 / 启动恢复 / 订阅调度 / 托盘
                    │
                    ▼
             RuntimeConfigTransaction
       draft → AST merge → stage → mihomo -t
                    │
                    ▼
              ICoreRuntime (唯一 owner)
        stop/reload/start → controller ready probe
                    │
                    ▼
       promote desired/applied state or rollback
```

Profile、模式、端口、TUN、DNS、订阅更新、导入、重置和清数据都应进入这一条管线，页面和托盘不再各自拼接持久化、Core restart 和 Windows 副作用。

## 审查基线发布阻断项（实施前）

### P0 — TUN / 透明代理存在双 Core 所有权

确定性证据：

- `MihomoServiceManager.DeployAsync` 仅使用 `sc create ... start= demand`，没有 service start 路径；
- `NetworkTakeoverService` 要求服务同时 Installed + Running，否则会带“服务未运行”的状态文案退回系统代理；
- Windows 服务启动后，`MihomoWorker` 自己监管一份 Mihomo；
- 同一 TUN 分支随后仍调用 GUI `MihomoCoreService.Restart`，启动第二份 Mihomo；
- Disabled、安全退出和网络 observer 主要管理/观察 GUI Core，无法代表服务 Core；
- Settings、托盘和主控显示的是 persisted preference，不是 effective TUN state。

结果是：默认部署后 TUN 不会生效；外部手启服务后又可能双开、端口冲突、退出后继续接管。这里不能用“加一个 `sc start`”局部修复，否则只会更稳定地触发双实例。

推荐模型：定义互斥的 `CoreOwner = None | App | Service` 和 `Stopped → Starting → Ready → Stopping/Faulted` 状态机。Service 可作为唯一 Core owner，也可只作为提权 broker，但系统中任何时刻只能有一个 Mihomo owner；App↔Service 必须有带 ACL、session/generation、版本握手、start/reload/stop/status/log 的 IPC。

Clash Verge Rev 已将运行方式建模为[互斥的 `RunningMode`](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/mod.rs#L57-L62)，并按[启动决策](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/lifecycle.rs#L16-L41) [dispatch 至单一 owner](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/lifecycle.rs#L428-L449)；其 [service session/start](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/service.rs#L827-L941) 和 [service stop](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/service.rs#L1000-L1044) 可作为协议参照。Nyanpasu 同样把运行实例建模为互斥的 Child / Service，仅在 service enabled 且 IPC Connected 时选择 Service；[IPC 状态变化会触发 `run_core`](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/core/service/ipc.rs#L26-L128)，再[停止旧实例并重新选择 owner](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/core/clash/core.rs#L496-L535)。这里的 Connected/Running 不等价于 controller ready：[RunType / Instance](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/core/clash/core.rs#L38-L83)。

### P0 — Profile、订阅和运行配置不是同一个事务

- “设为活动 Profile”只更新 `ActiveProfileId` 并刷新 UI；当前 Core 不重载；
- 当前订阅更新只替换 Profile 文件；如果它正被使用，Core 仍运行旧内容；
- mode/port/TUN/import/reset/clear 分别持久化，缺少统一 desired→applied 提交；
- 最终 `config.yaml` 仍直接覆盖，不是 staging + atomic promote；
- 合并后的最终字节没有在 promote 前执行 `mihomo -t`；
- `Restart` 先杀旧 Core，再尝试新 Core；失败时不能保持旧运行态和旧 Profile 指针。

应使用一条事务：

```text
draft target
  → YAML AST 合成及 App-owned key guard
  → staging file
  → mihomo -t
  → reload；必要时切换唯一 owner/restart
  → authenticated controller readiness
  → 原子 promote runtime generation + applied state
  → 失败保持/恢复旧 generation、旧 Core、旧指针
```

Verge 的 [Profile draft/commit/失败恢复](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/cmd/profile.rs#L216-L332) 和 runtime config 的 [validate→apply→commit](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/config.rs#L267-L340) 可作为局部行为参照；其 [reload 失败后 restart 的补偿](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/config.rs#L351-L372) 并不保证 restart 再失败时旧 Core 仍运行，不能直接当成完整运行态 rollback。Nyanpasu 的 client mutation 在 commit 影响 current Profile 时会[触发 rebuild，并把失败暴露为 degradation](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/client/mod.rs#L830-L865)，同样不代表完整运行态 rollback；其 concrete bridge 会对 [exact candidate 执行检查、重读、hash 与 promote](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/client/core_bridge.rs#L155-L187)，使用 [atomic product write](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/client/core_bridge.rs#L76-L89)，成功后先 [publish Promoted](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/client/mod.rs#L1495-L1525)，应用成功后再 [publish Applied](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/client/mod.rs#L1412-L1420)。

### P1 — YAML overlay 仍是过渡实现

本轮 defensive parser 已处理常见顶层 app-owned keys、普通及转义引号键、冒号前空格、带注释的单一 `---`/`...` 文档标记、owned mapping 内的空行/注释，以及带 node properties 的 indentationless sequence。Root merge、被删除 owned anchor 仍由保留内容引用、多文档、YAML directive 和当前无法安全比较的 complex/tagged/flow top-level key 会 fail closed。它关闭了所接受语法中的 controller/LAN 普通配置覆盖，但仍不等价于 YAML 语义合成。

剩余风险是行级 parser 无法证明覆盖未来 YAML 语法、嵌套 tag/anchor 组合、跨文档工具生成差异和 profile/global prepend/append；它也可能为了 fail closed 拒绝原本合法的高级 YAML。当前没有对 overlay 后 exact bytes 执行 `mihomo -t`，所以这仍是语义兼容与 final validation blocker，并非宣称仍存在一个已复现的 controller takeover。下一阶段应使用 YamlDotNet 或等价 AST mapping，建立显式优先级矩阵。Verge 的 [deep merge](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/enhance/merge.rs#L6-L35) 与 [完整 enhance 顺序](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/enhance/mod.rs#L714-L799)、Nyanpasu 的 [managed-field guard](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/nyanpasu-config/src/runtime/executor/builtin.rs#L78-L155) 可作为输入优先级参照。

### P1 — “恢复系统代理”没有恢复完整基线

当前 `WindowsProxyService` 只读写 `ProxyEnable` 和 `ProxyServer`；退出时只设置 `ProxyEnable=0`。这会丢失原有企业代理/手工代理的启用状态，也未覆盖 `ProxyOverride`、`AutoConfigURL`/PAC。还缺少 ownership marker，无法判断当前状态是否已被用户或别的程序修改。

应在第一次接管前持久化完整 WinINet tuple，并只在当前状态仍归 Clash# 所有时恢复。可扩展现有 durable mutation journal，不需要另造恢复框架。Verge 的 [PAC/global 模式顺序](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/sysopt.rs#L21-L35) 和 [系统代理/PAC apply/reset](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/sysopt.rs#L151-L253) 可作为行为参考。

### P1 — Windows repair 的 Reset 不是基线恢复

`WindowsNetworkDiagnosticService` 的 Terminal apply 会覆盖用户级 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 和 `NO_PROXY`，reset 则直接清空，不恢复原值；WSL apply 同时写这些变量和 `WSLENV` token，但 WSL reset 只删除 `WSLENV` token，仍留下 Terminal proxy variables；Microsoft Store loopback exemption 的 add/delete 也没有记录接管前是否已经存在或当前 owner。基线六语种文案称这些为 reversible actions，已在本轮改成中性的 apply/reset 描述，但数据与 ownership 风险仍未修复。

这些 Windows mutation 应复用 durable journal：apply 前记录完整 baseline、Clash# 写入值与 generation；reset 仅在当前值仍等于 Clash# 写入值时恢复 baseline。WSL action 必须把 Terminal variables 与 `WSLENV` 视为同一事务，Store exemption 也要区分“本来存在”和“由本次新增”。

### P1 — Core readiness、异常退出和进程所有权不足

- `MihomoCoreService.Start` 只观察约 1.2 秒，未探测 controller ready；
- Core 存活但未 ready 时，Windows 代理可能已被打开，形成流量黑洞；
- 没有长期 `Exited` 处理、退避重启或不可恢复时的代理回滚；
- 没有 Job Object / kill-on-owner-close；异常退出可能留下孤儿进程；
- stdout/stderr 只进入启动期 4 KiB buffer，运行期日志被丢弃；
- `Restart` kill 后没有明确等待退出/端口释放。

Verge 的 [readiness/退出处理](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/state.rs#L20-L225) 与 [Job Object](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/state.rs#L411-L448) 可作为 Windows 进程生命周期参照。

### P1 — 新 settings generation 架构尚未接生产

`ISettingsRepository`、`JsonSettingsRepository`、`DataGenerationManager` 和 desired/applied envelope 已存在于新项目，但生产 Host 仍主要注册 `AppSettingsService.Instance` 和 legacy adapters。因此设置导入、reset、clear、Profile/TUN/port 变更仍可能出现持久化、UI、Core、系统代理和 service 状态分裂。

建议暂停继续扩充 Settings 页面，将现有 generation manager 接入生产 composition，并让所有 destructive/settings operations 通过前述 runtime transaction。

### P1 风险 / 真机验证项 — TUN DNS 组合不完整

当前 TUN 只补 `dns-hijack: any:53`，并固定 `strict-route:false`，没有保证 `dns.enable`、enhanced mode、fake-IP range 和 IPv6 组合一致。源码能证明缺少受管默认与组合校验，不能单凭静态审查断言所有 Profile 必然失效或泄漏。应保留合法订阅 DNS；缺失时生成安全默认；校验 TUN/DNS 组合；将 strict-route 作为有 Windows DNS 泄漏权衡的显式策略，并以真机 DNS 泄漏测试决定最终默认。Mihomo 官方文档说明可参考：[TUN](https://wiki.metacubex.one/en/config/inbound/tun/)、[DNS](https://wiki.metacubex.one/en/config/dns/)。

## 审查基线功能差距（实施前）

| 优先级 | 能力 | 审查基线状态（实施前） | 建议 |
|---|---|---|---|
| P1 | 订阅自动更新 | `IsEnabled`、`UpdateIntervalHours`、`LastUpdatedAt` 只是元数据；基线 UI 文案曾误称自动更新，本轮已校正文案，但 scheduler 仍不存在 | supervisor + due scan + per-link single-flight + backoff；当前 Profile 更新必须进入 runtime transaction |
| P1 | Profile live activation | UI 选择不作用于当前 Core | 与 runtime generation 原子提交，不单独写 setting |
| P1 | Profile / subscription lifecycle CRUD | 页面只有 Profile 导入/校验/启用和订阅添加/检查/更新；不能重命名、删除，启用状态和更新周期也不可编辑 | 补 edit/remove、活动 Profile fallback、关联文件清理和 subscription schedule editing；运行态变更进入同一 transaction |
| P1 | effective 状态 | TUN/mode/profile 主要显示 preference | 所有 surface 展示 desired / applying / applied / degraded |
| P2 | Profile history / rollback | 基线六语种 Profiles 文案曾误称 rollback，本轮已校正文案；导入失败时的私有文件补偿不是用户可用的版本历史 | 保存来源、版本、hash 和应用结果；提供预览、回滚及 retention，并通过同一 runtime transaction 应用 |
| P2 | TUN 冲突诊断 | 基线启动冲突文案曾声称检查 TUN，本轮已校正文案；当前只检查外部 Mihomo 进程、mixed port 和错误的 Windows manual proxy | 唯一 Core owner 落地后检查既有 adapter、route、DNS hijack 和 service/controller ownership，并给出可恢复动作 |
| P2 | Connections 实时流 | 本轮已恢复入口和首次 REST 快照，仍无持续更新 | 仅页面可见时订阅 authenticated `/connections` WebSocket，节流、1s 重连、离开页面即释放；可参考 Nyanpasu 的 [WS lifecycle / 1s 重连](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/core/clash/ws.rs#L515-L662) 与 [token/Bearer authentication](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/core/clash/ws.rs#L714-L732) |
| P2 | Mihomo/service 日志 | Logs 主要是 App SQLite；Core 运行期输出丢失 | authenticated `/logs` stream + service ring buffer/snapshot |
| P2 | App/Core 更新 | 只有构建期 `Tools/Update-Mihomo.ps1`；无运行时更新链 | 若走 Store/MSIX 由渠道更新；否则必须签名 manifest、hash、rollback 和 service migration |
| P2 | 完整 service migration | 安装存在即认为可用 | 版本握手、binary/config generation、ACL、升级重装、ready probe |
| P2 | 数据包资源上限 | XML/Base64 总大小未设全局上限 | 流式读取、entry/total size 限制、解码前预算 |
| P2 | Installer 依赖告警 | RustSec 返回成功但报告 4 个 unmaintained warning 和 1 个 Unix-target `event-listener` unsound warning | 跟随 Slint/transitive dependency 升级，CI 单独跟踪 warning 基线，不把 exit code 0 等同于零 advisory |

Verge 的订阅 timer 分别实现 [overdue scan/timer 注册](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/timer.rs#L127-L190)、[single-flight](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/timer.rs#L294-L366) 和[当前 Profile 判定](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/timer.rs#L403-L420)，随后由 [`auto_refresh` handler](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/feat/profile.rs#L205-L245) 刷新 Core；Nyanpasu 的 [scheduler 支持 per-profile interval、overdue catch-up、周期任务与 stale-task abort](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/state/profiles/scheduler.rs#L131-L220)，并在 [post-start 阶段以 catch-up 启动](https://github.com/libnyanpasu/clash-nyanpasu/blob/6ea1bf1688208acc7d5e4bdf6739019fd3d1cc18/backend/tauri/src/state/profiles/actor.rs#L1084-L1091)。Verge 的 [Connections hook](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src/hooks/use-connection-data.ts#L297-L400) 包含订阅者计数和 idle-close；共享 [Mihomo WebSocket subscription](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src/hooks/use-mihomo-ws-subscription.ts#L229-L347) 负责最后消费者释放，[Logs hook](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src/hooks/use-log-data.ts#L50-L166) 则选择 `/logs` 数据流。

Connections 的 P2 是本次 MVP 修复排序，不代表它不重要；入口、首屏快照和关闭连接已可用后，实时流可以晚于两项数据面 P0。

## 首轮审查时已实施校正（历史阶段）

### GUI / app-owned Core controller control plane

- 新增统一 loopback endpoint；生成并持久化 256-bit 小写 hex bearer secret；
- secret 不进入用户 setting descriptors 和普通 reset audit event；clear-all 会删除并在下次使用时轮换；
- 生成态 `mihomo/config.yaml` 不再导出；旧 v1 包导入时安全忽略该文件，仍恢复 settings、profiles 和 providers；六种语言的备份范围文案同步校正；
- 默认配置和 Profile runtime overlay 强制 authenticated controller、`allow-lan:false`、loopback bind；
- 移除 imported `port`/`socks-port`/`redir-port`/`tproxy-port`/listeners/auth/controller（含 TLS/pipe/Unix listener 与 CORS）/TUN 等 App-owned runtime keys；
- `MihomoControllerClient` 的生产装配在 GET/PUT/DELETE 上统一加 Bearer；production transport 禁用 proxy、redirect 和 cookies，避免 loopback credential 被系统代理或重定向带走；
- 对 root merge、悬空 alias 和无法安全比较的 complex/tagged/flow top-level key fail closed，避免用文本 parser 猜测其语义；
- 增加 hostile profile、转义键、indentationless sequence、anchor/alias、mapping trivia、document marker、secret 形状、Authorization、transport 和配置生成测试。

首轮当时的剩余强化是固定 TCP 9090 的占用和伪装风险；当前实现已在连接后、首字节前按完整四元组验证 server PID，并独立验证 mixed TCP listener PID，因此占用只会 fail closed，不会泄漏 Bearer 或误提交 WinINet。迁移到 owner-specific named pipe + 当前用户 SDDL 仍可作为消除固定端口可用性冲突的后续统一项；Verge 的 [owner-specific pipe name](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/utils/dirs.rs#L302-L313) 和 [named pipe/当前用户 SDDL](https://github.com/clash-verge-rev/clash-verge-rev/blob/903410c12ea7cb767276ccefff0f205584a7dcb4/src-tauri/src/core/manager/state.rs#L118-L134) 可作为参照。

### Connections 可达性与统计一致性

- 增加主导航、route map、tray page、localized shell label 和 route architecture gate；
- 页面 Loaded 首次刷新，Unloaded 取消；刷新/关闭单个/关闭全部统一进入同一 latest-wins page lifetime，离页或新操作可取消旧请求，旧 GET 不能回写；
- close 后刷新失败会保留 Unavailable，而不再被 Closed/ClosedAll 成功文本覆盖；列表改为可伸缩网格并为关闭按钮补 UIA name；
- 移除手工“保存快照”按钮及其累计计数写入路径；后台 sampler 继续独占 cumulative→delta counter stream；
- 页面未新增 process-wide service locator；累计 connection counter 写入仍仅由已有后台 sampler 路径执行。

### 测试可信度修复

`ActiveConnectionModel_DoesNotAccessDisplayServices` 已更新到迁移后的 `ClashSharp.Core/Domain/Connections/ActiveConnection.cs`，不再因旧路径产生假红。

### 产品文案与能力对齐

首轮结束时，六种语言的 Profiles/Links 页面先收敛为只陈述当时已有的能力；Windows repair 不再承诺尚不存在的可逆恢复，URL blocking 明确为只遮罩显示文本且不阻断网络，启动冲突检查也不再声称已有 TUN probe，中文 README 把 startup restore fallback 从“常驻服务”校正为登录时运行一次的 helper。随后按推荐方案实施时，订阅自动更新、live activation、周期编辑、CRUD、版本回滚和 Windows mutation baseline 已补入实现；7A 又补入只读 TUN interface 提示、稳定 failure code 与页面内 Service-host 日志。App 软件更新则严格遵循 3A，只检查固定 GitHub Release，不自行安装。

## 原建议实施顺序（历史基线）

1. 先完成唯一 `ICoreRuntime` owner 和 App↔Service IPC；在此之前不要单独“启动已部署服务”。
2. 建立 RuntimeConfigTransaction，接入 Profile activation、订阅更新、mode/port/TUN/DNS、import/reset/clear。
3. 用 YAML AST 替换行级 overlay，并对最终 candidate 做 `mihomo -t`、atomic promote、controller readiness 和 rollback。
4. 扩展 Windows proxy baseline/ownership，补 Core crash rollback 和进程监督。
5. 将新 settings/data generation 接入生产 Host，统一 desired/applied/effective 状态。
6. 再实现 subscription scheduler、Connections/Logs WebSocket、更新链和 service migration。

## 自动门禁（不改变 Windows 系统状态）

这些门禁只允许写编译输出、测试临时目录和隔离的打包 staging；不得调用生成的 Installer，不得执行 `sc create/delete/start/stop`，不得启用 TUN，不得写真实 WinINet/注册表/环境变量/route/DNS/loopback exemption。涉及 Windows API 的测试必须使用注入 fake、probe 子进程或临时目录。建议从 repository root 按顺序执行：

1. **仓库与格式**
   - `git diff --check`
   - `dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore`
   - 检查 token、controller secret、runtime YAML、provider cache 和签名私钥没有进入发布产物或用户数据导出；四项 GeoData 只能进入由 Installer trust anchor 覆盖的 MSIX/release payload，不得进入用户备份或普通数据导出。
2. **Release x64 生产装配**
   - `dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore -m:1`
   - 必须 0 warning / 0 error；Recovery Watchdog、Mihomo Service、App 和所有 probes 都从真实项目引用构建，不能只编 linked source。
3. **全量 .NET 测试**
   - `dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-restore --no-build`
   - 必须 0 failed / 0 skipped；记录 fresh passed count，不沿用本报告的历史数字。
4. **决策边界定向门禁**
   - 1A：settings authority/admission、import/reset retained WAL、禁止 envelope dual-write。
   - 2B：watchdog exact PID + creation-time lease、clean disarm、stale lease/no-owner no-op、journal ownership restore；测试不得写真实 WinINet。
   - 3A/8A：固定 GitHub API/page URL、禁 redirect、response size/timeout、stable semver/ETag/failure-silent；架构测试禁止 App 下载/执行更新及 Settings Service Deploy/Uninstall。Installer 门禁覆盖 whole sibling payload exact-set/hash、可信 AppxManifest version、registered machine content hash、`Prepare → Add-Appx → helper verify → machine commit → Verified/clear` 顺序、strict journal codec/单向 phase、SCM fence、Add-Appx uncertain 时 marker retained、App read-only Fatal gate、watchdog 不 arm、shutdown 不触碰 network，并确认没有用户可选 previous-version rollback 或旧 Python loose-publish 入口。
   - 4A：inline/HTTP/file provider guard、relative path/reparse/size/copy-change、typed provider update、GeoData manifest/name/length/SHA-256/implicit DNS requirements/missing repair code；发布打包还必须证明四项 exact GeoData 输入全部存在。
   - 5A/6A：SID/SCM/PID first-byte identity、session/generation/hash binding、broker allow-list、wrong PID zero-byte、5 秒 incomplete frame、endpoint occupied code；不把 name-squat 可用性测试误写成机密性失败。
   - 7A：stable diagnostic-code grammar/classification/localization、Logs 页面 Core + Service merge/bounds/redaction/cancellation、active TUN interface 只读项无 repair action。
5. **Rust / Installer 静态门禁**
   - 在 `ClashSharp/Installer` 和 `ClashSharp/SandboxTest` 分别执行 `cargo fmt --check`、`cargo clippy --locked --all-targets -- -D warnings`、`cargo test --locked --all-targets`。
   - 对两个现有 `Cargo.lock` 执行受控版本的 `cargo audit`，单独记录 allowed warning baseline；已知 vulnerability 必须为零。
   - Release Installer packaging 只在隔离 CI/staging 中使用显式 pinned Mihomo version + SHA-256、四项 exact GeoData、受控 MSIX signing certificate 与显式 pinned Windows SDK；SignTool 只能从对应 Windows Kits x64 固定目录解析且必须通过 Microsoft Authenticode trust 校验，不能从 PATH 选择。`manifest.json`、允许文件名、length、SHA-256、未声明文件、缺失 payload、development certificate 和未配置正式签名都必须 fail closed。对最终 `ClashSharp-Installer.exe` 执行带可信 timestamp 的 Authenticode 签名并验证签名与 artifact hash；打包完成后不执行 Installer。
6. **自动门禁证据**
   - 保存命令、SDK/tool 版本、commit/worktree 标识、passed count、Rust advisory baseline 和产物 hash。
   - 任一门禁依赖真实管理员权限、Service、TUN 或系统网络状态时，应改列到下节人工矩阵，不能在开发机自动“顺便验证”。

## Windows 人工验收矩阵（会改变系统状态，仅限可回滚 VM）

开始前必须创建 VM snapshot，记录 WinINet/PAC/ProxyOverride、用户/系统环境变量、adapter/route/DNS、Service/SCM、ProgramData ACL 和登录启动项基线；每个 case 结束后恢复 snapshot 或证明所有 baseline 已恢复。

| 范围 | 必测场景 | 通过标准 |
|---|---|---|
| OS / 账户 | Windows 11 native x64 build 22000 或更高版本；ARM64、x86 与低版本拒绝；管理员凭据辅助安装 + 标准用户日常运行；中文/英文用户名与带空格路径 | 安装、启动、IPC、路径和本地化行为一致；App 平时不要求提权；原生 API 只接受 AMD64，ARM64 与 build 22000 以下明确拒绝 |
| Installer 生命周期 | clean install；旧版本→当前版本；同版本 repair；缺失/损坏 App、Mihomo Service、GeoData；Prepared 前后、Add-Appx 进行中/成功未回执、SCM fence、payload swap、association、Service Running、Verified/clear 各点分别 kill parent/helper/package child/断电；恢复过程再次中断；uninstall；reboot 后先尝试 App 再 Repair | 只有 Installer 修改安装状态；任何非 Verified journal 都使 App 在 Core/Service/系统代理/透明代理（TUN）前阻断；same-release Repair 从每个 cut 单向收敛到 exact package + machine + owner，或继续保留可诊断 marker，不得清 marker 后留下混代/partial machine；Service `binPath`/版本/owner SID/ACL/GeoData 同步；staging/backup 无非事务残留；卸载遇 pending 必须拒绝而非拆掉一半 |
| Installer/App 并发 | App 已运行时 Install/Repair/Uninstall；UAC 等待与取消；批准后 kill non-elevated parent；kill elevated helper；package PowerShell 运行中 kill parent；A/B 两会话并发 Repair；alternate-admin 凭据 | 不强杀 App；App、parent、helper 与 package child 对同一 startup barrier 的 file-id/volume 认知一致。parent 消失时 kill-on-close Job 终止仍属其控制的 package script，已提升 helper可完成当前窄阶段；无论哪者先消失，durable marker 都阻止新版 App 抢跑。跨 session machine mutation 串行；`WAIT_ABANDONED` 在已转移 mutex ownership 下 reconcile；pending 状态 fail closed 且可由同一 Installer Repair，无永久内核锁 |
| App / Installer 边界 | Settings 检查 Service 状态；Service 缺失；About current/new/unavailable/rate-limit；点击 release 链接 | App 无 Deploy/Uninstall/Repair/Update 执行路径，不下载 asset；缺失 Service/GeoData 指示运行 Installer Repair；只打开固定 GitHub release URL |
| 单交互用户 | 用户 A 安装并运行；用户 B 登录尝试连接/接管；A 仍运行时由 B Repair；Installer 显式把 owner 从 A 迁移到 B；RDS/快速用户切换 | 未迁移前 B 不能无声控制 Service；旧 owner App 运行时 reassociation 在任何 machine mutation 前拒绝；任一时刻只有一个受支持 owner；UI/发布说明明确 TUN 是机器级且不支持并发多用户隔离 |
| Service IPC / controller | 真实 LocalSystem；SYSTEM-only child controller SDDL；错误 SID/token/session/generation/hash；伪 pipe/wrong PID；child restart 换代；未完成 frame 占用 | wrong server/client 在首字节前或 broker 前 fail closed；伪 server 收到 0 application bytes；5 秒释放 incomplete frame；只允许 typed broker 命令 |
| App controller / listener | 预占 9090；mixed TCP 预占；mixed UDP-only 预占；旧 generation listener；端口释放延迟 | controller/mixed PID + epoch gate 拒绝假 ready，不泄露 Bearer/HTTP，不提交 WinINet；错误包含稳定 endpoint/mixed code |
| TUN 单 owner | Disabled→App；App→Service TUN；Service→App；反复切换；SCM Start/StopPending/unknown；Core/Service crash；App 正常退出/崩溃/关机 | 全程最多一个 Mihomo/Job/TUN owner；只有 authenticated exact readiness 后才接管；未知状态 fail closed；无 orphan adapter/process |
| Recovery Watchdog | `TerminateProcess`、Task Manager kill、未处理异常、helper 启动失败、stale/reused PID、正常退出、系统重启 | crash 后 one-shot helper 只恢复仍由 exact ClashSharp journal 拥有的 WinINet；正常退出不误恢复；helper 失败时下次启动 reconcile；不触碰用户后续外部修改 |
| Windows mutation | manual proxy on/off、PAC、ProxyOverride、企业策略；Terminal vars、WSLENV、Store exemption 原先存在/不存在；接管中外部修改 | apply/restore 保留完整 baseline；只撤销 ClashSharp-owned 值；外部修改不被覆盖；失败/断电可由 journal 恢复 |
| TUN / route / DNS 冲突 | WireGuard/OpenVPN/Tailscale/ZeroTier/WARP/sing-box 等已运行；IPv4/IPv6；DNS hijack/fake-IP/strict-route 组合；残留 adapter/route | UI 只读、可解释地报告已知 TUN 名称和 stable code，不自动停止其他 VPN 或删 route/DNS；ClashSharp 无 DNS 泄漏/错误默认路由，失败时保持原网络 |
| Provider | inline proxy/rule；正常 HTTP/HTTPS；404、TLS、超时、重定向、userinfo/非法 scheme；file 相对路径、越界、reparse、过大、复制中变化；typed update | 合法 mainstream provider 可用；非法 URL/path/file fail closed；HTTP cache 仅在 protected runtime；App 不能构造任意 controller 请求；错误映射到 provider code |
| GeoData | MMDB/dat 两种模式；GEOIP/GEOSITE/IP-ASN、sub-rule、classical provider、DNS implicit GeoIP、sniffer；缺失、篡改、未声明和版本升级资产 | 只使用 Installer manifest 声明且 hash/length 匹配的资产；按 exact config stage；不触发 LocalSystem geo auto-download；损坏时提示 Installer Repair |
| Profile / transaction | 有效/无效 Profile；活动订阅更新；mode/port/TUN/DNS 变更；Core restart/readiness failure；history rollback | exact generation 成功才 commit desired/applied；失败保留旧可用运行态；UI 不显示虚假 applied |
| Logs / diagnostics / UI | App owner 与 Service owner；大量 Core/Service 日志；secret/path/URL/error 注入；离开页面；窄窗口、键盘、UIA、六语种 | Logs 合并 App/Core/Service 且可取消；Core 日志有界并规范化控制字符，Service child/host 另对 authority secret 脱敏；不暴露 token/raw controller response；稳定 code 与行动文本可复制、可定位且布局可用 |
| 启动 / 包装 / 重启 | packaged 双实例；登录启动；startup restore helper 与 watchdog 竞态；Explorer restart；Service/系统重启；升级后首次启动 | 单实例、owner lease 和 recovery 顺序确定；无重复 Core/helper；Explorer/重启/升级后 Service 路径和系统网络状态一致 |

## 本轮最终验证记录

以下均针对 2026-08-05 最终工作树；只运行构建、解析、单测和只读检查，没有运行 Installer 或执行任何系统 mutation。

- worktree/commit 标识：`main@b45e6bec50d660542579e9187828123aefcc282f`，其上为本轮尚未提交的实施工作树；SDK/tool 为 .NET SDK `10.0.201`、Cargo `1.95.0`、rustc `1.95.0`；
- `dotnet restore ClashSharp/ClashSharp.slnx --locked-mode -p:Platform=x64`：通过，所有项目已是最新；
- `git diff --check`：通过；仅报告 11 个工作树 CRLF→LF 的后续 Git normalization notice，无 whitespace error；
- `dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore`：通过，exit 0；诊断 verbosity 复跑同样 exit 0；
- `dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 -p:UseSharedCompilation=false --no-restore -m:1 -nr:false`：通过，`0 warning / 0 error`；
- `dotnet test ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 -p:UseSharedCompilation=false --no-restore --no-build -m:1 -nr:false`：`2229 passed / 0 failed / 0 skipped`；
- Installer `cargo fmt --check`、`cargo clippy --locked --all-targets -- -D warnings`：通过；`cargo test --locked --all-targets` 为 `31 lib + 27 main + 4 integration = 62 passed`。Debug/test build 仅按设计提示本地 payload 缺少 `binaries/service/clashsharp.mihomoservice.exe`，不会生成可发布 trust anchor；
- SandboxTest `cargo fmt --check`、`cargo clippy --locked --all-targets -- -D warnings`：通过；`cargo test --locked --all-targets` 为 `11 passed`；没有启动 `.wsb`；
- PowerShell 只解析门禁：`build.ps1`、`Prepare-GeoData.ps1`、`Update-Mihomo.ps1`，以及 APPLY、UNINSTALL、PACKAGE_PROCESS_PREFLIGHT、parent mutation wrapper、transaction write/clear/protection 等全部内嵌模板均为 `0 syntax error`；没有执行模板；
- `cargo-audit-audit 0.22.2` 使用 advisory DB `6d7aef354b4144c1ede046034adfd00246d3b0c0`（1189 advisories）：Installer 与 SandboxTest 均为 `0 vulnerability`。Installer 记录 5 项 allowed warning baseline：`RUSTSEC-2025-0141` bincode、`RUSTSEC-2024-0436` paste、`RUSTSEC-2026-0206` rustybuzz、`RUSTSEC-2026-0192` ttf-parser、`RUSTSEC-2026-0221` event-listener；paste/rustybuzz/ttf-parser 来自 Slint transitive tree，event-listener 只在非 Windows 的 Slint tree，event-listener 与 bincode 均不进入 `x86_64-pc-windows-msvc` target tree，SandboxTest 无 warning；
- pinned Mihomo `v1.19.27`：实际 length `47213056` 与 SHA-256 `842fa17493a82c97148e76e3c523f5058e3cf386fba611cab86b6681e75f2f77` 均与 manifest 完全一致；
- pinned Windows SDK `10.0.26100.0` x64 SignTool 的只读 trust probe 为 ordinary non-reparse file、`Status=Valid`、Microsoft signer、存在 timestamp；其 signer certificate 已于 2026-05-06 UTC 到期，验证结果证明脚本必须依赖可信时间戳后的 `Valid` 状态，而不能错误要求 signer certificate 今天仍有效；
- 正式 packaging/signing：**未运行**。仓库当前没有 `Binaries/GeoData/manifest.json` 和四项真实资产，也未提供受控 MSIX/Authenticode release key；正式入口会按设计 fail closed，因此没有伪造 artifact hash。现场发现的旧 Cargo 未签名 `target/release/ClashSharp-Installer.exe` 已从正式名称移走并明确标成 `ClashSharp-Installer-Development-Unsigned.stale.exe`；新脚本会清理 legacy 正式名，只在 staging 内签名验证成功后 promotion。

自动门禁不得运行 Installer、Service、TUN、WinINet、注册表、route 或 DNS 操作。

本报告不宣称 Windows 服务、TUN、注册表恢复或交互 UI 已经过真机验证；这些仍是发布门禁，不可由源码审查和绿色单测替代。
