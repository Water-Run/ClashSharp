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
- 长按阈值、选中配色和拖动跟手效果尚未在 Windows 11 与 Server Desktop 上实机确认，
  本轮只交付代码与单元测试，视觉验收留待下一次安装候选。
