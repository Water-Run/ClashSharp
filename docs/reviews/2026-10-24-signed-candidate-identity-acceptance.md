# 内部签名候选固定身份的实际生产与消费

## 上游准确CI与字节证据

字节验证代码e4caee891d7aa42a35f9285e1281de6b7f0a5de9的[完整CI40022979388](https://github.com/Water-Run/ClashSharp/actions/runs/40022979388)成功，scope head一致、product=true，四份原始TRX经服务器核验 **7,263／0／0**。报告归档SHA256为 `349A7A5362F879F5FC4FA4153999DEA0FAE464AC0F0156FFBCD5959A2D9AA921`。

真实CLI下载分支的[正例40023024705](https://github.com/Water-Run/ClashSharp/actions/runs/40023024705)消费完整73c707b，四归档／两scope／四报告通过；字节证据artifact12531701597，ZIP SHA256 `839EBFBFAA28F44A3778B0BD1FCBB0FD4843264D6923030D9B483D0017699950`，JSON SHA256 `6E0238991A9F27D068C47A29EA24173CC498FE3E1298C4F24023A60574AC8C11`。绿色文档[反例40023030539](https://github.com/Water-Run/ClashSharp/actions/runs/40023030539)在元数据阶段准确拒绝，下载skipped、零产物。开发库存没有被当作签名或发布授权。

随后[40025021879](https://github.com/Water-Run/ClashSharp/actions/runs/40025021879)消费新完整源码e4caee8／run40022979388的四归档，提供本次准确同源码的已验证CI输入，没有混用73c707b报告或候选身份。

## 准确检出、内部签名与固定身份

新建准确e4caee8的Git检出，tree为eb3d5267c199e683b9276e32cd2103f433479de6，1,957份tracked文件。固定材料复制与locked restore后tracked状态仍干净。工具在原SID交互式受控内部任务中执行现有build.ps1，实际单文件载荷审计通过；在任务原有临时信任仍有效时验证八项产物的Windows签名观察和策略pin，以及包来源身份／Runtime字节。

清单实际SHA256与返回身份完全一致：

`ad19068277ccc4ab381d83bed709cb39b7a24173a738bbca9defc0918b46d8ca`

Installer SHA256 `BFD9B6728A2699B02B6BAED00B6F51E84E56FB3C36EFAC3105D283AF0701628E`，MSIX SHA256 `968881BFDCAC180D24C26FE9412E3B108B01143DB53234C4352BA4F0B0CB3263`。规范清单绑定源码／tree、CI证据、八项文件与签名身份；acceptanceBound及promotionAuthorized保持false。

生产任务正常退出0，自有Root和PFX副本全部移除。随后实际消费工具用独立返回的摘要重新打开清单和八文件，字节重验通过。signatureTrustRechecked=false，未把清理后的当前信任冒称重新验证。候选没有被安装或启动。

26项独立合成契约、组合范围72项及实际RepositoryTopology16／0／0在服务器通过；合成签名观察没有混计为真实签名验证。源码输入来自准确旧提交，但新增生产／消费工具本身还需其新提交的准确CI，不能用旧源码CI替代。主MSIX时间戳按实际记录，未声称全部正式签名策略已闭合。内部信任也不自动成为公开发行信任。

## 旧缓存清理与原状态

仅清理四个已关闭节点项目层bin／obj：120目录、11,364文件、逻辑6.48GiB。删除前确认节点关闭、无引用任务／进程，全部绝对目标在固定项目层，无重解析点或受保护文件；8,215份源码／签名release／备份／当前数据前后摘要一致。当前构建、源码、release、证据及用户数据没有删除。

清理与构建并发，C盘观测净增4.09GiB不是独立物理回收量；清理结束46.43GiB，全部构建结束45.04GiB可用。计划、目标和摘要核对收据均保留。

原47数据逐文件保持、原安装仍ebbbb65、服务Auto/Running/PID4232，四类证书多重集8／4／2／42保持，无产品进程。签名任务导出后移除。没有正式Release、签名环境、VM或本机产品／Sandbox操作。

接续点为服务器 `C:\ClashSharpValidation\candidate-identity-20261024\checkpoint.json`；candidate-identity.json为清单，checkout/artifacts/installer/release为真实产物。允许列表证据下载核验22项／21文件，ZIP SHA256 `208EA56138ADBFD42A15BD9A6FFAE331763A62C2C4B78A5B058F19BF243B2D01`，本地为`artifacts/verification/server-candidate-identity-20261024`，不含私钥或原用户数据。

下一层仍需将实际平台／恢复验收引用该固定候选，再由受保护流程提升同一字节；Win11、完整恢复矩阵和公开发布仍未放行。
