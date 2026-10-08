# GeoSite 重建材料与服务器恢复接续

准确 `df93bf0633ed5b4611daa7d0b802c447f05dd918` 的 [CI 37719909553](https://github.com/Water-Run/ClashSharp/actions/runs/37719909553) 两项作业已成功。下载后的四份 TRX 再次核验通过：主程序 4,031、Installer Core 1,093、Presentation 184、Windows 1,323，共 **6,631 项通过，零失败、零跳过**。CI 自行生成的摘要与四份实际报告的运行身份、数量和 SHA-256 完全一致。

结束采样的六项失败、超时、取消和成功用例均实际执行。原先超时的用例本次用时 0.383 秒。Installer Core 行覆盖率为 93.55%，分支覆盖率为 86.35%。九个开发发行文件、最终 MSIX 内的第三方说明及四份 GeoData 都已核对摘要；开发版 Installer 仍未签名，原生验收另行完成。

## GeoSite 重建结果

已准备固定版本的生成器、社区规则、其他输入及依赖源码，完成现有 `GeoSite.dat` 的重建验证。以下证据区分原始输入记录、候选输入和可复现构建，不把相同输出等同于已经证明所有历史下载。

1. `Loyalsoldier/domain-list-custom` 的历史发布 `20260904020013` 仍保留，发布数据 SHA-256 为 `7b31b09624535e65e23c9ad732610ff1dcf95c2f3089d53a2f5a740f6a55a941`，其标签绑定源码 `efacb51b8950ae673ebb6dcb9e7ecdd1decb1b6d`。根据该源码的文本导出算法恢复 `geolocation-!cn.txt`，随后与上游不可变 Git Blob `df0ac98d8eb4f44d63badb560f5784fed5811b08` 逐字节匹配。该输入为 24,425 条规则、540,561 字节。
2. 固定社区源码 `cb663f66025ef3be1c1c7eb367dfac5f46645ffc` 和其余候选输入，执行原生成流程的规则处理步骤。第一轮 1,549 个规则组中有 1,548 个完全吻合；GitHub tracker 镜像使 `TRACKER` 多出 37 条规则，不能直接替代原流程的 Gitea 输入。
3. 诊断构建仅使用固定的主 tracker 规则源时，所有 **1,549 组、219,665 条规则**的类型、内容、属性和重复次数均与发布数据一致。原 Gitea 响应仍未取得，这个结果证明可用这些固定输入重建已发布内容，不证明原流程曾使用哪些具体网络响应。
4. 上游生成器遍历无序映射，重复生成的序列化顺序不同。独立记录原输出的组与规则顺序；构建时先验证生成数据的完整原始规则字节集合和重复次数，再按顺序记录输出。规则内容全部来自重新生成的数据。
5. 多次重建都得到 4,243,484 字节，SHA-256 为 **`54af8c41407b9a56a59e65c03bcc27d812cb2620e18e62e4795e2972fff5b539`**，与发布输入及 `df93bf0` 最终 MSIX 内的文件完全一致。

当前环境对原 Gitea 地址的请求返回空响应，不能据此反推 9 月 6 日的行为。[固定的上游生成流程](https://github.com/MetaCubeX/meta-rules-dat/blob/4178770badecb1b349fbcd62c737e0d7a2079729/.github/workflows/run.yml) 和[当时的 tracker 项目说明](https://github.com/XIU2/TrackersListCollection/blob/77ebb519b509915a7afdfd49c1044cb0fd9cd042/README.md) 均已核对；GitHub 镜像差异没有被计为通过。

## 内部重建材料包

内部归档为 `artifacts/verification/geosite-reconstruction-20261008/GEOSITE-RECONSTRUCTION-MATERIALS-v2.zip`，共 **1,851 个文件、2,705,800 字节**，SHA-256：

```text
2457891a7770fd4a7c7431cfce11569e90aa253f39f9bed34531fcaafe3be974
```

归档保留固定源码、规则输入、五个 Go 模块的 vendor 内容、原许可证文本、文件摘要清单、构建入口及独立顺序记录。原始参考 GeoSite 二进制没有作为构建材料打包。两个确定性归档逐字节一致；最终归档重新解包，在新的工作目录和空缓存中、关闭 Go 模块下载后完成编译与重建。实际使用 Go 1.27.0、Python 和 Git Bash，结果仍为上述准确摘要。

额外 37 条规则的输入被规则集合核验拒绝；修改冻结输入后在构建前被拒绝；Python 优化模式也被入口拒绝，避免关闭验证断言。完整失败尝试、比较结果和重建收据保留在同一验证目录。

此包是内部重建材料，尚未加入发行资产。原 Gitea 输入历史、部分输入的确切历史版本和相关发行条款仍未全部闭合。固定的 `xishang0128/rules` 树未找到独立许可证文件，其原始 README 已保存。现有 `geoDataUpstreamInputRevisionsRecorded` 保持 `false`，G09 没有整体关闭。

## 服务器恢复后的状态

用户确认维护结束后，已重新连接指定 Server 2025，核对主机、测试账户 SID、安装版本和主程序集摘要。原 `8289dec` 包仍在，服务 Auto/Running，应用与内核退出，公共安装事务不存在。旧验证目录确实不存在，因此此前中断的目录准备没有被当作已完成。

已新建受保护的 `C:\ClashSharpValidation\continuation-20261008`，取得关闭状态的 **158 文件备份**并逐文件复核。原安装器已从已知输入恢复，准确摘要和 Authenticode `Valid` 均核对；新的 `df93bf0` 开发载荷已传输并验证。SFTP 通道不可用时使用服务器支持的 SCP 协议完成传输，SSH 身份验证和传输后摘要验证保持。

通过既有 SSH 密钥能够使用原测试账户执行只读查询。其完整代理指纹仍为 `C4CB7760F87D6A3AD3C80022F6747BBD78F8383916AA2476C087206C1ABEDC64`，代理未启用。签名证书引用存在，但会话 0 的私钥打开/签名探针未成功，不能据此断言密钥可用或已经丢失。

当时缺少 `CshAT260922` 的交互桌面会话，曾请求用户登录。用户随后指出既有 Administrator SSH 入口可用，已据此恢复服务器构建和回归；原测试账户桌面只限制相关界面步骤。服务器已有 Administrator 本地会话可由 SSH 调度一次性任务执行本地 IPC 测试，准确源码的 6,625 项服务器回归已通过。后续签名与清理见 [Administrator SSH 接续](2026-10-08-administrator-ssh-validation.md)。现有产品及数据没有替换，本机 Sandbox 不再用于原生验收。
