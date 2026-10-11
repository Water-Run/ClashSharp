# Windows 11 全界面审查与优化

用户明确要求完整审查并按 Windows 11 风格修改，范围包括全部主页面、公共控件、常用弹窗和安装器。关于/设置的首轮改动只是一部分；本表用于记录实际发现、修改及新候选复验，不以控件存在性或旧截图代替视觉通过。

## 设计基线

采用微软的[Windows 11 设计原则](https://learn.microsoft.com/en-us/windows/apps/design/design-principles)、[层次与表面](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering)、[内容间距](https://learn.microsoft.com/en-us/windows/apps/design/style/spacing)和[排版](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)。保留 Mica 窗口、原生导航/菜单/对话框、系统主题与强调色；统一标题层级、正文可读宽度、4/8/12/16/24 间距、操作位置及状态反馈。数据列表需要利用可用空间，设置/说明内容需要适当限制宽度。

安装器使用 .NET 自带的[WPF Fluent 主题](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)，同时保持已有安装事务、权限检查、键盘操作及高对比度支持。服务器上的 WinUI/WPF 真实渲染可验证布局，不能代替尚未完成的 Windows 11 平台矩阵。

## 覆盖清单

| 界面 | 检查重点 | 当前状态 |
|---|---|---|
| 窗口与导航、托盘、启动反馈 | Mica/主题、边界、选中状态、图标、菜单、标题与缩放 | 源码与实机复查中 |
| 主控 | 状态区、模式按钮、全部磁贴、编辑器与状态显示设置 | 待完整复查 |
| 节点 | 分组/节点/资源区层次、列表密度、空状态与底部反馈 | 待实机复查 |
| 配置 | 工具栏、当前配置、表头/行对齐、选择及历史/重命名 | 已观察宽屏，待完整复查 |
| 链接 | 工具栏、空状态、订阅卡片、添加/编辑弹窗 | 待实机复查 |
| 规则 | 表头/行、长文案、窄窗口、命中数对齐 | 待实机复查 |
| 触发器 | 列表、添加卡片、编辑器、条件/动作选择与确认 | 待实机复查 |
| 连接 | 搜索/筛选、不可用/空状态、列表与关闭反馈 | 待实机复查 |
| 统计 | 汇总与分组、图表/表格、长标签、日志入口 | 待实机复查 |
| 日志 | 搜索筛选、级别、详情、清理预览与返回 | 待实机复查 |
| 关于 | 信息卡片、单/双列、更新与核心状态、代理信息弹窗 | 首轮中英/深浅/尺寸检查通过，待整套一致性复查 |
| 设置 | 全部分组、长说明/操作换行、重置/导入/连接测试弹窗 | 首轮可见行检查通过，待完整复查 |
| 安装器 | Fluent 主题、主次操作、诊断展开、进度/恢复及确认状态 | 旧默认主题已确认，待优化复验 |

实机基线为 `c34d95a` 内部候选，证据在服务器 `continuation-20261011/runs/full-visual-driver` 及工作区 `artifacts/verification/win11-ui-audit-20261011`。新增截图和发现将逐项更新；未检查的内容保持待验证。
