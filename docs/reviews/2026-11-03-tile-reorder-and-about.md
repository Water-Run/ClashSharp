# 磁贴交互与关于页修复（2026-11-03）

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
