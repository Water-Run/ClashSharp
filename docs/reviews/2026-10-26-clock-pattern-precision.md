# 自定义时钟格式的小数秒和字面文本

按用户最新要求，本轮继续后台SSH开发，以Server2025作为统一验收环境，不再新增Win11重复测试门，具体见[当前验收方式](2026-10-26-validation-scope.md)。本机Computer Use保持停止，没有再次控制窗口或启动安装版应用。

## 发现

DisplayFormatter.Time为保留触发条件的小数秒，原先直接将系统LongTimePattern中的每个ss替换为ss.FFFFFFF。这会改写引号中的ss／class等字面文字；合法的单s或%s秒标记没有扩展；已有fff／FFF的格式则重复输出小数部分。系统自定义长时间格式省略秒时，也无法看见实际小数秒。格式标记、引号、反斜杠及百分号语义参考[Microsoft自定义日期时间格式](https://learn.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings)。

例如，16:05:06.1230000在HH:mm:ss.fff下原本显示16:05:06.123.123；HH:mm:ss 'ss'中的字面ss也被修改。问题来自实际源码及服务器合成区域格式，不是声称已改动用户Windows格式。

## 修正

逐格式标记扫描，跳过引号内容及反斜杠转义，识别百分号单标记。已有小数秒只扩展其标记到7位，保留f固定补零和F可选尾零的差异；没有小数标记时在实际秒字段后加入。系统格式省略秒时保留区域前后缀／AM或PM位置并补足时钟精度。不修改CultureInfo对象、存储时间、时区、单位比例、机器序列化或用户系统设置。

只在TimeOnly包含不足一秒的tick时应用该处理；既有整秒、12／24小时选项、区域数字、KB／KiB与速率格式路径保持。源码功能回归直接引用实际Application项目，没有创建或控制桌面窗口。

## 验证与接续

准确旧903209e源码加11项新回归，服务器 **10失败／1通过**；通过项是既有反斜杠转义场景，没有把它伪装成已复现缺陷。修正后完整DisplayFormatter集 **32通过／0失败／0跳过**，包括原21项单位／时钟用例。新用例覆盖单秒、引号字面文本、已有固定／可选小数位、逗号分隔、百分号秒及前后置AM／PM；并确认区域格式对象不变。

两份修改C#源码在服务器按长度／SHA核对，输入清单`fc98e98b89cee14d38844e488732fb25837b6356d1552e04053953f06b54074e`。完整解决方案format0退出，诊断0字节，无工作区加载警告。测试工作树为903209e加清单覆盖；准确新提交的完整CI需另行核验，32项不是全量产品报告。

服务器测试根为`C:\ClashSharpValidation\clock-format-20261026`，Probe仅链接仓库原测试和实际Application项目，使用同版固定xUnit／Test SDK及固定.NET10.0.201。原始red／green报告、输入和日志保留，没有本机产品测试、Sandbox、安装或原数据变更。当前安装仍db664eba／3061，不含本次时钟修正；最新原47备份仍为agent-replay-candidate-20261026/after-native-review，服务44480。整体目标继续。

## 准确 CI 与手验候选

修正提交`92ec65abdb2d6e4202bdcb19038db851c7268ceb`的准确完整CI [40261746609](https://github.com/Water-Run/ClashSharp/actions/runs/40261746609)两job成功。四原始TRX分别4,665、1,135、193、1,344，合计 **7,337／0／0**；四artifact字节／API摘要、两份完整scope、原始报告和开发包清单经独立工具核验，CI证据SHA256 `8fc7c90f0fce391a7778abbe1fffce8ba402782a5d4b9afdf210a307625f43c8`。

首次原始报告下载遇到EOF，保留其0字节文件并核对产物ID／预期大小；按原API产物重新下载通过，没有重跑CI或构建。下载诊断不纳入公开证据，避免传播短期签名下载地址。

18个源码测试证据文件及清单共19项已双端逐项核验。ZIP23,674字节，SHA256 `11e5afb8153d7f4ae3321fb9b185d698adef2c930d21f2239681e3ac612bbfe0`，清单`7784b79b26b4a99906e7dc9827e8540e000e317b98b3b47ff0eedc43806d6eb2`。原red TRX `31e851195a652c1f2b67a99829e3c7a734c8ff0a43e7c181069e0e2545f52584`，green `0d5fb50b593b38492a7e0a193756c1b5f61d67b192752031fe9cadd0be9f6950`，原失败记录不覆盖。

已单次发出绑定92ec及准确CI的内部签名流程 [40263501178](https://github.com/Water-Run/ClashSharp/actions/runs/40263501178)，用于整合用户手验候选；只记录已创建并API确认queued，不预报签名、安装或用户验收成功。后续按同一作业观察，完成必要候选检查后按[手验清单](2026-10-26-manual-handoff-checklist.md)交付；用户反馈的功能／样式问题修复确认前，不进入正式公开发布。
