# 主控磁贴的开关与拖动

手验发现主控信息磁贴用开关控件表达状态，右上角另有一个只用于拖动的抓手图标。两点都不符合磁贴规范：
状态应由磁贴本身的选中外观表达，拖动应对整块磁贴长按生效。

## 交互修正

- 状态磁贴不再绘制开关。`IsToggleOn` 直接驱动磁贴外观：选中时以强调色填充整块磁贴，标题、数值、
  详情和趋势线切换到 on-accent 前景色；未选中时恢复常规主题色。
- 移除右上角抓手及其 `ReorderHint` 绑定，磁贴不再带任何拖动手柄；`GridView` 的原生
  `CanDragItems`／`CanReorderItems`／`AllowDrop` 一并关闭，避免留下只对边角生效的半条路径。
- 拖动改为整块长按：按下 450 ms 且位移不超过 8 px 后进入重排，移动时把被拖磁贴逐步换到指针所在磁贴的位置，
  松手时经 `SetVisibleInfoTileIdsAsync` 一次性提交；指针取消则回滚到已提交顺序。
- 点击、键盘和 UIA 行为保持原有按钮语义；重排开始时先抑制随后的释放点击，避免长按误触发状态切换。
  重排只在 `CanEditTileLayout` 为真时启动。

## 验证

- `MasterControlViewModel.MoveVisibleInfoTile` 只重排本地可见顺序，持久化仍由既有保存路径负责；
  新增 `InfoLayout_LongPressMoveReordersLocallyWithoutSaving` 覆盖换位、未知标识和零保存。
- 18 项目 Release x64 构建零警告、零错误；应用测试 4745 通过／0 失败／0 跳过，
  `dotnet format --verify-no-changes` 无变更。
- 长按阈值、选中配色和拖动跟手效果尚待原 Server 2025 上的新候选实机确认，
  按既定验收范围不额外要求第二套 Windows 11 环境。

## 整合时补查

候选整合前发现 `FindInfoTileAt` 接收的是网格局部坐标，却直接传给
`VisualTreeHelper.FindElementsInHostCoordinates`。该 API 即使限定子树，也要求窗口坐标；
导航区、页边距或滚动偏移会造成命中位置错误。现先通过
`InfoTileGrid.TransformToVisual(null).TransformPoint(position)` 转换至 XAML 根坐标，再查找磁贴。
依据[命中检测 API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.visualtreehelper.findelementsinhostcoordinates?view=windows-app-sdk-1.8)
和[坐标转换 API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.transformtovisual?view=windows-app-sdk-1.8)。

原 c2a556b 的准确完整 CI40631898108 两任务成功，四份原始报告已核对为7466／0／0。
为避免交付已知命中缺陷，已取消尚在准备阶段的内部候选40645289673，没有接受其产物。
坐标修正在服务器独立源码副本编译通过，零警告、零错误，完整格式检查通过，源码摘要
`38c9665fdc2149736925632f2fd64dad8fbc41ae31da3f00b751fd3609dd25f9` 前后保持。
修正提交8fd697a的完整CI40646054846两个任务成功，四份原始报告4745＋1141＋206＋1374，
共7466通过／0失败／0跳过；四份CI归档的原始字节、范围及开发包清单已独立核验。
GitHub元数据接口曾间歇返回EOF，退出的是本地观察器，原CI没有重跑；只读获取增加有限重试后
沿用同一套规范校验器完成核验，没有修改生产获取工具或降低校验要求。
内部签名候选40648798006已从这份准确CI启动，签名、载荷与原生验收结果仍待完成。
原服务器当前应用和最新手验数据保持，真正的长按交互仍须以整合候选的原生操作为准。

后续8fd整合候选已完成原生安装与数据恢复，实际长按移动仍未获得通过证据，详见
[整合原生验收](2026-10-28-integrated-native-acceptance.md)。在保留本次选中外观和整块长按语义的基础上，
补齐按钮到网格的指针捕获转移、单指针身份、捕获丢失／Esc／卸载取消回滚及激活和按压样式复位。
两个最终源码在服务器逐项匹配，编译、115项相关回归和完整格式检查通过；尚待新补丁的完整CI、签名及原生输入复验。
