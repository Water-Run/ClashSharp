# 导航与菜单入口清单及键盘补验

## 当前入口与已有证据

按当前源码核对入口，而非将历史文档的“其余菜单待验”作为无限待办。`ShellRoute`有11个稳定路由；Logs有普通和Trigger两种参数上下文。侧栏有十个不同页面目标，另有`NavProxiesItem`父项与节点子项共享ProxyNodes标签，不算新的路由。

| 页面目标 | 当前侧栏入口 | 本轮实际页面标记 |
|---|---|---|
| 主控 | NavMasterControlItem | RootScrollViewer |
| 节点 | NavProxyNodesItem | RefreshRuntimeButton |
| 配置 | NavProfilesItem | ImportProfileButton |
| 订阅 | NavLinksItem | AddLinkButton |
| 规则 | NavRulesItem | RulesList |
| 触发器 | NavTriggersItem | OpenTriggerLogsButton |
| 连接 | NavConnectionsItem | RefreshConnectionsButton |
| 统计 | NavStatisticsItem | RefreshStatisticsButton |
| 关于 | NavAboutItem | AboutScrollViewer |
| 设置 | NavSettingsItem | SettingsScrollViewer |

十个目标均逐项导航、等待实际标记并核对选中项。紧凑侧栏不暴露隐藏子项时，临时展开侧栏读取已选择的节点，再关闭；没有将“找不到隐藏UIA节点”记为丢失导航选择。普通日志与Trigger日志另行核对父项及筛选，构成两种日志上下文。父级代理入口的标签别名已核对源码，本轮没有另计一次原生点击。

当前菜单／浮层按以下五类追踪；普通ComboBox、颜色控件、文件选择器和确认弹窗各有自己的记录，不混入自定义菜单数量。

| 类型 | 当前实现 | 对应证据及范围 |
|---|---|---|
| 信息磁贴详情 | Components/MasterInfoTile.xaml(.cs) | [10月23日详情与菜单](2026-10-23-live-option-presentation.md)：b441主题修正后的十帧、正文／链接、原位翻译、导航后关闭；其余磁贴功能状态不由这十帧全部证明 |
| 多选列表排序菜单 | Components/SearchableOptionList.xaml.cs / CreateReorderMenu | [10月23日联合复验](2026-10-23-live-option-presentation.md)：文字绑定、草稿、行身份与离页关闭；非空搜索及选择器后续证据见[选项编辑器](2026-10-23-option-editor-lifetime.md) |
| 触发器行操作 | View/Triggers.xaml.cs / MoreTriggerActionsButton_Click | 同上菜单文字／可用态，及[10月24日确认目标](2026-10-24-confirmation-targets-and-status-language.md)：删除提示与取消；新目标正文没有更改菜单绑定与命令id |
| Windows通知区菜单 | Service/SystemTrayService.cs / ShowContextMenu | [托盘原生交互](2026-09-29-native-selection-and-tray.md)、[功能顺序](2026-09-29-tray-feature-order.md)及10月23日联合复验：顺序、显隐、分隔符、主题及选择；本轮未重新执行代理模式命令 |
| 命令栏“更多”菜单 | App.xaml / ClashPageCommandBarStyle，页面CommandBar/AppBarButton | 动态折叠使用WinUI默认控件；本轮新增俄语实际折叠、启用态、明暗主题、Escape返回焦点和Enter重开检查 |

这些是入口与证据范围清单，不代表每种运行状态、每个菜单命令或所有系统／DPI组合全部完成。后续以具体功能／矩阵缺口推进，不重复开启已经有对应证据的基本菜单检查。

## 当前候选的实机补验

复用已接受的 `ebbbb65f4362730e7334baa8d1691995ceda390f`、1,942份输入、签名／BOM和准确CI **7,263 / 0 / 0**（run40003975639），无产品重建或重复全量回归。服务器新节点 `navigation-matrix-20261024` 先核对原47文件、主程序集和服务，再通过正常安装器进入隔离数据；SDK仍在原数据上先于凭据读取拒绝，测试期间MCP范围7、代理关闭、没有代理控制授权或剪贴板读取。

同一PID7948、800×600DIP／96DPI，简体中文亮色、德语暗色、波斯语亮色分别完成：

- 统计打开普通日志后，日志内容和统计侧栏选择同时存在，类别为当前语言的“全部类别”；聚焦已选择的统计项并真正发送原生Enter，实际回到统计页。
- 触发器打开日志后，保留Trigger筛选和触发器侧栏选择；原生Enter激活已选触发器项，实际回到触发器页。
- 两类日志各重新进入一次，通过页面返回按钮回到真实来源。合计六次原生Enter和六次历史返回，没有用UIA Invoke代替键盘输入。

本轮关闭了9月30日记录中“尚未把Enter激活已选父项计为通过”的具体缺口。快捷键严格验证当前窗口、进程、启动时间、焦点和前台归属，只发送Enter／Escape。

俄语长标签实际使配置命令栏出现“更多”。主栏边界保持在窗口内；260px侧栏按紧凑模式作为临时覆盖层打开和关闭，未将其视为内联压窄内容。折叠菜单先显示禁用的历史项，选择唯一内置配置后该项正确启用；明暗主题文字可读，菜单完整处于画面内。原生Escape三次均回到More按钮，原生Enter两次均重新打开菜单。没有调用历史项或更改配置。框架“收起”等启动期文字沿用现有的语言刷新说明，本轮没有覆盖系统控件资源。

十七张DWM可见应用画面已逐一查看：十二张三语言日志／父页，俄语页面和侧栏两张，菜单禁用／启用／暗色三张。页面文字、筛选、焦点和布局在本轮范围内通过，未发现需要修改的产品缺陷。

辅助脚本失败保留：PowerShell5.1不支持`ushort`别名，首次在发键前拒绝，改为UInt16并从当前日志页续接；标准命令栏没有暴露预期Popup类型，但下拉命令实际已出现，改按真实下拉项位置和画面核对，没有重放原“更多”点击；下拉关闭后历史按钮被UIA虚拟化，选择已发出后仅重新打开并读取启用结果，没有重复选择。另一次预检参数绑定错误发生在任务调度前。四阶段正常安装和已有成功场景均未重跑。

## 最终状态与接续

四次正常安装器、合成应用和原数据恢复后的应用均正常退出0。当前安装仍ebbbb65，原47文件显式恢复无额外文件，37项设置、9份历史、数据库原行保持，待处理0、原MCP关闭且默认范围1／端口17892；两份运行时缓存新增并退役两份旧缓存，无新增历史。代理、桌面、环境及证书集合保持；十八项任务导出后清理，失败结果保留，产品进程退出，服务Auto/Running/PID4232，C盘约58.92GiB可用。

最新关闭备份为 `C:\ClashSharpValidation\navigation-matrix-20261024\after-native-review`（47文件），接续点为同目录 `native-checkpoint.json`。本地 `artifacts/verification/server-navigation-matrix-20261024/native-evidence.zip` 已下载并逐文件核验292项／291文件，SHA256 `A96ED70F62AC2CD12AF5A8EB496CAD042EB0F7372BD44237060EABC8C9A51BAC`。归档不含原用户数据或私钥，本机没有产品或Sandbox执行。

实际Windows11、其余显示／恢复组合和正式发行门禁仍待完成。下一节点应对当前1.0.0验收要求归并证据，推进实际缺失的发布／恢复行为，而非继续笼统列“导航和菜单待验”。
