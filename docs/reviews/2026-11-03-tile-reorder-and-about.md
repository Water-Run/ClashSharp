# 磁贴交互与关于页修复（2026-11-03）

## 最新用户修正：Metro / Windows Phone 交互

用户在第一版实机交互验证后明确拒绝右上角双竖线和拖动线框，要求高度参考 Windows Phone 10 / Windows 10；关于页继续要求收紧留白、优化信息展示。此前 d025 的线框效果不作为最终交付。本节覆盖下面旧版实现说明，当前新动效尚待候选构建与实机验收。

- 磁贴整面可操作，移除双竖线、拖动描边、落点线框和重阴影。按相对坐标计算三维倾斜：中心下沉、边缘与四角向按压位置偏转；抬起恢复。正反位置、缩放、无效尺寸和越界捕获有独立几何测试。
- 长按进入临时编辑状态，选中磁贴浮起、其他磁贴缩小。编辑状态中点击只选择，不触发代理开关或详情；鼠标或触摸可以继续拖动。Esc、空白处及“完成调整”退出，不新增持久设置。
- 拿起点保持在鼠标／手指下方，磁贴平滑放大；移动后本体使用 65% 不透明度，原位留空，周围元素平滑让位。松开即接受并持久化顺序，视觉独立在 160ms 内落位；导航、取消和渲染暂停具有页面所有权与超时边界。
- 普通权限与管理员进程采用同一窗口内动效，避开 OLE 对管理员进程的限制。GridViewItem 继续提供键盘与辅助功能语义，仅键盘交互显示系统焦点提示。
- 关于页改为紧凑的居中 SVG 标识；应用与内核版本、运行时、简述和作者／许可证集中展示。更新与本地代理为第一组，项目与上游归属为第二组。直接显示可复制的本地代理地址，保留路径详情；更新不可用时明确给发布页入口和 Setup.exe 更新步骤。

参考微软的 [Tilt 效果说明](https://devblogs.microsoft.com/vbteam/visual-basic-windows-phone-7-series-5-how-to-create-a-control-tilt-effect-application-for-windows-phone-7/) 与[拖动／重排动画说明](https://learn.microsoft.com/en-us/windows/apps/design/motion/xaml-animation)。本项目独立实现，没有复制旧 Toolkit 源码或引入其运行时依赖。

## 第一版历史验证（用户已拒绝其线框视觉）

精确 d025 完整 CI41560303547 的原始 4 个 TRX 为 4832＋1141＋229＋1382，7584／0／0。签名41562445686生产及独立载荷消费通过，候选 d2e067ad，原 ZIP164b77db，Setup7021A717。蓝机已正常更新并恢复当时最新63文件，复核全部37设置、2配置、1订阅及2历史、代理摘要一致。真实鼠标按住、快速与长按往返拖动、Esc、最小化、移出窗口松开已通过。关于页居中截图也已查看，但用户进一步要求更紧凑布局。普通点击曾因前台保护在输入前被拒绝，重启持久化尚未执行；这些验证不能代替上述新 Metro 交互的实机检查。

用户手验确认此前磁贴按住与拖动没有可见反馈，不能以此前的构建和布局持久化测试替代鼠标验收。本轮仅通过 SSH 控制蓝机，不使用 Computer Use，不改变 RDP 会话或系统 UAC 策略。

## 实现

- GridViewItem 统一承担点击、键盘、焦点和辅助功能；移除覆盖整块磁贴的子 Button，避免捕获争抢。
- 普通权限使用 WinUI 的 CanDragItems、CanReorderItems、AllowDrop、ItemsWrapGrid 及原生拖动生命周期。
- 蓝机当前进程令牌实测为 elevated。WinUI 原生拖放不支持该环境，因此该分支使用窗口内捕获、相对容器几何命中、移动阈值和长按判断；磁贴预览随鼠标移动，原位淡化、落点边框、重排过渡和边缘滚动均可见。
- 松开仅在页面与数据代次一致且真正改变顺序时保存一次。Esc、系统取消、捕获丢失、移出窗口后松开、最小化和页面卸载恢复已提交顺序。长按及拖动不触发磁贴动作。
- 所有磁贴均有按下反馈，提供拖动图示和八种语言提示。鼠标移动达到阈值即可开始；触摸移动先保留滚动语义。
- 关于页限制阅读宽度，Logo、产品名称、版本、运行时及描述垂直居中；作者与许可证居中，两列信息卡片随可用宽度改为单列。

## 验证状态

源码编译、格式检查及相关回归正在核验。此前完整应用测试中 4831 项通过，1 项目录结构约束在新增独立 View 部分文件时失败；已将交互实现合并回配对代码文件，正在重跑相关检查。实际新候选、真实鼠标拖动及截图尚待完成，不能记为通过。

## 参考

- [微软 GridView 重排属性契约](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listviewbase.canreorderitems?view=windows-app-sdk-1.8)
- [微软 StartDragAsync 的管理员权限限制](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.startdragasync?view=windows-app-sdk-1.8)
- [Windows App SDK 对管理员进程拖放问题的跟踪](https://github.com/microsoft/WindowsAppSDK/issues/4433)
