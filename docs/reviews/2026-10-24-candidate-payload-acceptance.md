# 准确候选的实际只读执行与平台记录

## 实现与服务器契约

候选重验收敛为一个共享的带租约消费入口；只读Installer执行在清单和八文件持续锁定期间进行，退出后再核对库存才保存passed收据。新收据核对真实平台、主MSIX摘要及外层payload的数量／字节；已有输出在进程启动前拒绝，原进程与诊断可直接观察。

服务器最终候选身份契约33项、收据契约33项、组合CI范围73项通过。合成契约没有计为真实签名或平台验收。最初契约发现嵌套Consume变量遮蔽，修正后才开始实际进程。首次实际审计退出0，但新收据错误地限制机器组件数不得大于外层文件数，拒绝；第二次同样退出0，发现规范OrderedDictionary的length键不能由Measure-Object按普通PSObject属性读取。修正为逐项读取键并求和，增加准确7／4计数及字典／JSON两形态、摘要／数量／总量不一致的用例。两次拒绝与三份进程记录均保留，没有将旧失败改为通过，也没有安装／恢复操作重放。

## 实际固定候选执行

复用来源e4caee891d7aa42a35f9285e1281de6b7f0a5de9的已生产内部签名候选，独立身份为：

`ad19068277ccc4ab381d83bed709cb39b7a24173a738bbca9defc0918b46d8ca`

最终实际运行GUID b81cd625-7056-4b17-ac32-6fca27f68f94／PID3304，2026-10-23T21:34:36.7215369Z至21:34:38.2994411Z，正常终止、退出0、stderr空。报告4个payload文件、251,474,016字节和7个包内机器组件；主MSIX摘要968881bfdcac180d24c26fe9412e3b108b01143db53234c4352ba4f0b0cb3263与候选准确匹配。

规范执行收据实际SHA256：

`a9e41efb20d337e56075d61e1a35d5923fe2cbc8005d7b8ce7ab66ad8ff6aad0`

宿主观察为10.0.26100／ProductType3／X64，归入windows-server。installationVerified、win11AcceptanceSatisfied、promotionAuthorized均false。未将本轮执行冒称安装、UI、恢复或Win11通过，也未重新验证当前证书信任；信任来源仍是先前受控生产操作及其独立候选摘要。

实际重入已完成输出目录在启动前拒绝，四份完成文件摘要保持。三次自有进程4164／13492／3304均已终止退出0，无进程或任务遗留。该只读阶段未安装新候选，当时安装为ebbbb65；原47文件逐项一致、服务Auto／Running／PID4232、证书多重集8／4／2／42保持。随后该固定候选的实际安装和原数据恢复见[服务器平台案例](2026-10-24-candidate-platform-baseline.md)。

对准确95bfd05源树叠加本次PowerShell／工作流文件后，使用既有实际测试程序集在独立服务器源树运行RepositoryTopology，16／0／0通过，没有重编译产品或改动旧验收源树。源树下559份既有运行文件仅硬链接复用，未扩大产品测试结论。

服务器接续点为`C:\ClashSharpValidation\candidate-audit-20261024\checkpoint.json`，准确收据位于`actual-payload-audit-r4/payload-execution.json`。允许列表证据包33项、32文件逐项核对，ZIP SHA256 `DE3FB516B0FF226ED8894ED943CAF8414765312AD8D994E5E0C8ECC9E73589CA`；本地归档为`artifacts/verification/server-candidate-audit-20261024/audit-evidence.zip`，不含原用户数据和私钥。首次本地校验因绝对路径分隔符混用拒绝，归档字节保持，规范化同一路径后32文件全部匹配；没有重新下载或重打包。

设计与后续门禁边界见[候选载荷执行设计](../design/2026-10-24-candidate-payload-acceptance.md)。本工具的准确94638fb／CI40028743000现已完成，两项job及四原始TRX7,263／0／0通过，详见上述平台案例。当前平台仍无法替代实际Win11，整体发行目标尚未完成。
