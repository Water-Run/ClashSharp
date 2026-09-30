# 最小窗口与连接恢复原生复验

本轮在蓝色 Windows Server `WIN-07OBU2PV8C6` 的专用验收账户、会话 8，实际安装并运行 `650e1d6bbb002329ebdef7a735f7eb871197e2bc`。源码归档 SHA256 为 `C877F99F233FF53450AA672B5F6C565216282E5A493B4967C0D22A7B559CB173`。安装器、MSIX 和依赖安装后的签名状态均为 Valid，安装 DLL 与该候选包一致，仅验收账户注册；使用临时验收签名，不代表正式发行证书。

[CI 36659298836](https://github.com/Water-Run/ClashSharp/actions/runs/36659298836) 对同一提交成功完成两个作业，独立核对四份 TRX：主项目 3426、安装核心 1031、安装呈现 152、Windows 安装 1126，合计 **5735 项通过、零失败、零跳过**。

中文浅色与德语浅色的 800×600 原生窗口中，日志返回按钮分别显示“返回”和“Zurück”提示；设置重置操作是蓝色文字链接。首页更新磁贴在尚未检查时仅显示一次“未检测”，实际检查失败后显示不可用状态并保留检查时间。本次网络未成功取得 GitHub 更新结果，不把失败提示验收当作成功更新验收。

保持德语连接页打开，通过本机回环地址和混合端口建立受控的 39 字节双向回显连接；无需手动刷新即显示活动行。托盘停止内核后旧行自动清空、关闭按钮禁用、进程回执确认内核退出。托盘恢复待机后，再次建立的连接自动出现，关闭按钮恢复；“全部关闭”使连接归零，两个测试端正常结束。此候选证据使 `connections.09` 通过，功能矩阵为 **91 通过、35 部分完成、15 待验**。其余行未因本轮测试而提前提升。

正常退出后应用与内核进程均为零，系统代理和 TUN 均关闭。已安装的 `ClashSharpMihomo` 代理服务仍在运行，不能称为服务卸载或停止。两个数据库完整性均为 `ok`；故障测试累计留下六条 Connections Warning（包含恢复的旧测试记录），没有 Error。关闭状态备份 19 文件、1301682 字节；15 文件旧数据的恢复是手工验收夹具恢复，不作为自动迁移通过证明。

证据使用明确允许列表归档。`evidence-650e1d6-native-pages.zip` 含 19 份 JSON，SHA256 `1F24C07119975F27EE6EB16B7AD101AD8862EEEED624D57E300E0D4AA202D3DA`；`desktop-evidence-native-pages-650e1d6-20260930T025728Z.tar.gz` 含 60 原始截图及 60 条操作，SHA256 `E6E8B8B2E10972654B73DA84443524937B551076C8378050837A4E2CE036F45D`。独立 `Verify-650e1d6-NativePages.py` 校验哈希、包身份、签名、连接回显、进程、数据库、备份、截图及矩阵；回执 `native-pages-650e1d6-verification.json` 明确保留整体生产验收为 false。

本次视觉检查还发现两处需要修复：从正常窗口缩至最小窗口时，首页内容宽度未随可用区域收缩，模式卡片右侧裁切（`desktop-20260930T024551Z-277674596.png`）；统计页节点的更新时间占用独立自动列，使流量数值被挤压（`desktop-20260930T024811Z-988187420.png`）。后续源码将首页布局约束到滚动容器扣除页面边距后的宽度，节点更新时间改到独立行。按 [Microsoft SizeChanged 契约](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.sizechanged?view=windows-app-sdk-1.8)处理窗口实际宽度变化，不从可能超宽的子内容反推可用宽度。两处修复需要后续候选重新安装、截图复验。

后续工作树安全验证：18 项目 Release x64 构建 86.11 秒，零警告、零错误；日志 ViewModel、页面生命周期、异常分类、日志架构和首页布局策略共 76 项通过、零失败、零跳过，回执 `artifacts/verification/minimum-layout-20260930/logs-and-minimum-layout.trx`。未在开发者本机启动产品、安装器或改变系统设置。该本地回执不能代替后续候选的 CI、打包或原生验收。

随后在同一服务器安装 `0be3807fe24738b7e66b17ef9e897a83fcfdfb12`，源码归档 SHA256 `C2C61FC5E10CA43862E1D5D0AFA1453E002A0B07D716A2B58092B0775C7D0EB7`。安装后的三个签名均为 Valid，DLL SHA256 `88A14B47FB246885CC792C3BAB2EB689F474DC35AA6785E13CA2190337FB9B53` 与候选 MSIX 一致，仍仅专用验收账户注册。德语浅色 800×600 窗口中，首页四个模式卡片可完整显示；放宽窗口后再缩回最小尺寸也完整。统计页两个节点的上传、下载和完整更新时间均可见。第一次缩至约 994 像素宽、侧栏仍展开的中间截图仍有右侧裁切，不能据此宣布所有窗口宽度和侧栏组合都通过。

日志页实际进入三次，每次四个 PID 绑定的 TCP 样本均有一个应用到回环控制器 9090 的连接，三次本地端口分别为 53926、54453、54461。分别通过返回按钮、切换设置页、返回按钮离开后，各四个样本的连接均为零；应用 PID 17032、内核 PID 26520 在观察期间保持一致。通过混合端口向受控回环 HTTP 服务发起请求后，页面实际显示 Mihomo TCP 日志。此证据把 `logs.08` 从待验提升为部分完成，矩阵为 **91 通过、36 部分完成、14 待验**；服务 IPC 轮询取消及服务所属内核未直接观察，保留待验范围。

从日志页点击侧栏已选中的统计项没有返回统计页，发现一个新的导航缺陷；返回按钮和切换其他页面可工作。`b20120e` 的修复补充 NavigationView 的 ItemInvoked 入口，继续保留 SelectionChanged 的键盘选择行为，重复目标由现有导航逻辑去重。参照 [Microsoft ItemInvoked 事件契约](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.navigationview.iteminvoked?view=windows-app-sdk-1.8)。修复是否通过原生验收须以之后的安装候选为准。

本候选正常退出后应用和内核均为零，代理和 TUN 关闭，已安装代理服务仍运行；两个数据库完整性为 `ok`，仍为六条既有 Connections Warning、无 Error。关闭状态备份 19 文件、1305778 字节。允许列表归档 `evidence-0be3807-native-minimum.zip` 含 17 JSON，SHA256 `7D63BE5422D0E0887B67D3B3BE158666880ED30E94D79E6623135803EEAA4287`；桌面归档 `desktop-evidence-native-pages-0be3807-20260930T034713Z.tar.gz` 含 26 截图及操作，SHA256 `7331E76FD2725759653CA531A4179FC5932122EFEEC1D05F6C0875DD9DD67166`。`Verify-0be3807-NativeMinimum.py` 独立核对上述证据及矩阵，整体生产验收保持 false。

`b63057bcd5909827943eedc436b5755d37017279` 仅更新布局资源检查，产品和安装器源码与 `0be3807` 完全相同，但服务器已安装二进制身份仍是 `0be3807`。[CI 36663965001](https://github.com/Water-Run/ClashSharp/actions/runs/36663965001) 两作业成功，四份 TRX 为 3438、1031、152、1126，合计 **5747 项通过、零失败、零跳过**；回执 `ci-b63057b-verification.json`。`0be3807` 的 CI 被后续推送并发规则取消，没有报告成失败或通过。

`b20120e0e62920144581fadb721e287831eaed67` 随后真实安装到同一服务器，源码归档 SHA256 `EF86A2D56FCF6196B35CC39C9C3035C75E52880B1AA74EEE2FBB1B2C73BB0DA4`。安装 DLL SHA256 `2901B10D3963D972CFF726B43BC851E88DE34354A6DFD99CF28DA84561A1B9C0` 与该 MSIX 匹配，安装后三个签名均为 Valid，仅验收账户注册。[CI 36666153850](https://github.com/Water-Run/ClashSharp/actions/runs/36666153850) 两作业成功，四份 TRX 3466、1031、152、1126，合计 **5775 项通过、零失败、零跳过**；本地独立候选构建零警告、零错误，相关 401 项回归通过。

德语浅色最小窗口中，从统计进入日志后，直接点击侧栏已选中的统计项成功返回统计；同一应用 PID 12128、内核 PID 17324，日志打开时四个样本各有一个控制器连接，返回统计后四个样本均为零。方向键移动焦点再按 Enter 可正常进入连接页。直接缩至最小窗口时四个模式卡片完整；订阅用量完整详情和 Escape 后焦点恢复也实际观察。放宽窗口后的第一帧仍显示未完成的宽度变化，重新观察后侧栏文字及内容正常，不能把第一帧当作稳定布局缺陷；其余宽度组合仍需复验。订阅用量标题末尾字母单独换行、详情缺少页面入口是该候选仍有的问题，由后续 `b8b4201` 修改，须使用新候选验收。

正常退出后代理和 TUN 关闭，应用及内核为零，已安装代理服务继续运行；两个数据库完整性均为 `ok`，既有六条 Connections Warning 无新增 Error。关闭状态备份 19 文件、1309874 字节。`evidence-b20120e-native-navigation.zip` 允许列表 13 JSON，SHA256 `6C51C0C07A8DD2945994CE9B9D5944E6C21B1C434C477D90A5F7BA2D91B5EB6C`；`desktop-evidence-native-pages-b20120e-20260930T045833Z.tar.gz` 含 27 原始截图及操作，SHA256 `89794E56BCCE1B341CF3E15672AFFC310B1E2C19299A21B96696073284766A10`。`Verify-b20120e-NativeNavigation.py` 独立验证身份、哈希、安装、连接释放、退出、数据库、备份和截图，回执 `native-navigation-b20120e-verification.json` 不提升功能矩阵，整体生产验收继续为 false。
