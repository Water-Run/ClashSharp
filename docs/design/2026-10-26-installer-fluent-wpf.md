# 简洁的 WPF Fluent 安装器

安装器继续使用 WPF，主程序继续使用 WinUI 3。本轮按用户要求参考 VS Installer 清晰的状态与操作关系，采用适合单个产品的平面单页布局。

## 页面布局

产品名称、版本、当前状态和重新检查位于统一页头，正文直接展示状态、说明和进度。安装、修复、卸载、取消和关闭固定在底部，通过细分隔线与正文区分。默认 760×520，最小 640×420。

按用户对层叠样式的反馈，移除产品卡片外框、详情底板和诊断代码的输入框外观。详情沿用 WPF 原生 Expander 模板，只局部覆写装饰画刷与边框；诊断代码保留原生只读 TextBox 的键盘选择和复制能力，以无边框等宽文字显示。

账户切换和旧副本卸载需要确认时，正文直接展示确认内容，暂时收起普通状态及诊断，避免重复说明与嵌套卡片。焦点在布局完成后落到“暂不切换”或“暂不卸载”。确认区消失或窗口开始关闭后，排队的焦点动作不会再执行。

界面使用 WPF 原生窗口、按钮、进度条、展开区和滚动条。应用设置 `ThemeMode="System"`，让 WPF 管理系统主题及强调色；只保留自己的品牌和间距资源。这里使用的是微软提供的 [WPF Fluent 主题与 ThemeMode](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)，并参考 [Visual Studio Installer 的产品维护入口](https://learn.microsoft.com/en-us/visualstudio/install/repair-visual-studio?view=visualstudio)组织操作。没有引入安装器标签页、工作负载选择或组件管理器。

## 实际控件渲染

下图来自服务器上实际编译的 WPF 窗口，使用模拟安装状态。它们用于核对布局和主题，不表示已经执行系统安装。

![可安装状态](assets/installer-fluent-20261026-light.png)

![最小窗口的深色维护状态](assets/installer-fluent-20261026-dark.png)

![最小窗口的账户切换确认](assets/installer-fluent-20261026-confirmation.png)

![展开后的平面诊断区](assets/installer-fluent-20261026-diagnostics.png)

查看了八个状态的实际渲染，包含浅色、深色、最小窗口、执行进度、展开诊断、两种确认及完成状态。检查了固定操作与初始折叠详情的边界，确认按钮的键盘焦点，以及实际窗口关闭时的后台收尾行为。测试范围及遗留节点见[本轮审查记录](../reviews/2026-10-26-installer-native-review.md)。
