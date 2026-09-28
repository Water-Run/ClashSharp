# 原生列表操作连续性（2026-09-28）

蓝色服务器的 9d2ea7e 实测确认了三个待修缺陷：配置校验后列表跳回顶部；旧配置的状态保留导入时的语言；鼠标进入部分可见的资源列表后，继续滚动无法露出下方资源。未知地区同时显示内部代号 `UN`。配置页证据见[原生反馈验收](2026-09-28-profile-feedback.md)。

滚轮复现截图为 `desktop-20260928T082925Z-254717828.png` 和 `desktop-20260928T082946Z-116813652.png`；移至外侧右边距后，滚轮能显示末行资源，见 `desktop-20260928T083036Z-251941575.png`。这区分了实际嵌套列表问题与远程输入未送达。微软说明 [ScrollViewer 的滚动衔接不适用于鼠标滚轮](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.scrollviewer.isverticalscrollchainingenabled?view=windows-app-sdk-1.8)。

配置页面改为保留可观察集合及相同标识的行，通过属性通知更新名称、状态、计数和时间；新增、移动和删除只影响对应项目。状态写入使用稳定资源键，兼容八种语言的旧状态文本；读取目录时按当前语言显示已知状态及内置配置名称，保留用户名称和未知诊断文本。正常保存时归一化旧状态，不因浏览页面额外写盘。

三个有界节点列表在滚轮到达垂直边界时恢复事件冒泡，由外层原生 ScrollViewer 继续处理；不自定像素步长，保留 Windows 的速度、动画、虚拟化及键盘行为，避开下拉框和滚动条自身的输入。使用 [AddHandler](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.addhandler?view=windows-app-sdk-1.8) 仅针对这三处已确认的已处理事件。节点页脚同时对齐正文边距，未知地区在八种语言中使用明确名称，地区代号及中性旗标保持原有数据含义。

本地 Release x64 18 项目构建零警告、零错误（43.59 秒）；主程序回归 **3314/3314**，零跳过（1 分 18 秒），完整格式检查通过。新增回归检查八种旧语言状态的切换、保存和重开，未知状态与用户名称保留，以及列表刷新、重排、删除时的身份和选择。原生滚轮、配置位置保持及中英文显示需下一候选安装后复验；不以源码改动代替验收通过。
