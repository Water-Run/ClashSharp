# 配置切换的 YAML 锚点往返

服务器手验发现：「GW」订阅显示「校验通过」，但「设为当前」反复报「无法切换当前配置」。应用日志对应 12 条
`Mihomo configuration is not valid YAML. (Parameter 'configurationText')`。

## 原因

`MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys` 合并受管字段后，用
`YamlStream.Save(writer, assignAnchors: true)` 重新发射文本，再由
`MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration` 重新解析这段自己生成的文本。
YamlDotNet 18.1.0 会给重复出现的标量写锚点，而它的解析器读不了流式映射里「带锚点的值 + 后续键值对」。

- 最小复现：`proxies:` 下两条 `- {name: a, type: vless, port: 443}`，发射后出现
  `- {&780131563 name: a, &1199709688 type: &679821603 vless, ...}`，重解析报
  `While parsing a flow mapping, did not find expected ',' or '}'`。
- 同一份发射文本 PyYAML 与 bundled mihomo 都能解析：生成的是合法 YAML，失败点是解析器自身。
- 该校验路径与用户可见的「校验」入口不一致：`CoreConfigurationService.ValidateImportedProfileAsync`
  只对原始导入文本做形状校验和 `mihomo -t`，不做发射往返，所以该订阅「校验通过」却永远切不上。

该订阅把每个节点写成一行流式映射且字段值高度重复（`type: vless`、`port: 443`、同一 uuid），
因此必然命中。服务侧 `MihomoEffectiveConfigurationMaterializer.Serialize` 同样以
`assignAnchors: true` 发射，而它的产物随后由 `MihomoServiceConfigurationTrustValidator.LoadUniqueRoot`
重新解析，属于同一缺陷。

## 修正

两处发射改为 `assignAnchors: false`。YamlDotNet 在解析阶段已把别名展开成共享节点，
无锚点发射会写出重复内容，语义不变；18.1.0 是 NuGet 当前最新版本，升级无法回避。

## 验证

- 新增 `OverrideRuntimeKeys_FlowStyleRepeatedScalars_EmitsReloadableText`（TUN 开关两种取值）
  与 `MaterializeAsync_FlowStyleRepeatedScalars_StagesReloadableConfiguration`，两者在修正前均失败。
- 用服务器导出的真实 GW 配置（107 389 字节、98 节点、150 规则、115 条流式节点）走完整
  `OverrideRuntimeKeys`：修正前抛 `Mihomo configuration is not valid YAML.`，修正后通过并保留受管字段
  （`external-controller: 127.0.0.1:9090`、`mixed-port`、TUN 与 DNS）。该验证为一次性临时用例，未入库。
- 18 项目 Release x64 构建零警告、零错误；应用测试 4745 通过／0 失败／0 跳过，
  `dotnet format --verify-no-changes` 无变更。安装器原生清理 13 项在本机非隔离提权环境按既有守卫拒绝，
  未纳入通过数，且与本修正无关。
- 修正后应用侧与系统服务侧的发射路径一致，但实机「设为当前」仍需在下一次安装候选上复验。
